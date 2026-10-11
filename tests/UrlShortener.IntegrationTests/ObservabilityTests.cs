using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UrlShortener.Api.Contracts;
using UrlShortener.IntegrationTests.Infrastructure;

namespace UrlShortener.IntegrationTests;

/// <summary>Scenario 3, R2 + R3: health probes, correlation IDs, trusted-proxy client IPs, log hygiene.</summary>
public class ObservabilityTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiFactory _factory;

    public ObservabilityTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task HealthLive_Returns200WithoutCheckingDependencies()
    {
        (await _factory.CreateAnonymousClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_AllHealthy_Returns200WithEachCheck()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        var checks = doc.RootElement.GetProperty("checks");
        checks.GetProperty("database").GetProperty("status").GetString().Should().Be("Healthy");
        checks.GetProperty("cache").GetProperty("status").GetString().Should().Be("Healthy");
        checks.GetProperty("click-pipeline").GetProperty("status").GetString().Should().Be("Healthy");
    }

    [Theory]
    [InlineData(false, HttpStatusCode.OK, "Degraded")]            // default: stay in rotation, serve cached links
    [InlineData(true, HttpStatusCode.ServiceUnavailable, "Unhealthy")] // opt-in: original requirement §8
    public async Task HealthReady_DatabaseUnreachable_StatusDependsOnPolicy(bool failReadiness, HttpStatusCode expectedCode, string expectedStatus)
    {
        var client = _factory.WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:Links", "Data Source=/nonexistent-dir/down.db;Mode=ReadOnly");
                b.UseSetting("Database:MigrateOnStartup", "false");
                b.UseSetting("Health:FailReadinessOnDatabaseOutage", failReadiness ? "true" : "false");
            })
            .CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(expectedCode);
        var body = await response.Content.ReadAsStringAsync();
        JsonDocument.Parse(body).RootElement.GetProperty("checks").GetProperty("database").GetProperty("status").GetString()
            .Should().Be(expectedStatus);
        body.Should().NotContain("SqliteException", "exception details must not leak through probes");
    }

    [Fact]
    public async Task CorrelationId_WellFormedHeader_IsEchoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-ID", "order-checkout-42");

        var response = await _factory.CreateAnonymousClient().SendAsync(request);

        response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle("order-checkout-42");
    }

    [Theory]
    [InlineData("bad value with spaces")]
    [InlineData("inject\\nFAKE LOG LINE")]
    public async Task CorrelationId_MalformedHeader_IsReplacedWithTraceId(string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", value);

        var response = await _factory.CreateAnonymousClient().SendAsync(request);

        response.Headers.GetValues("X-Correlation-ID").Single().Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Theory]
    [InlineData(true, 3)]   // trusted proxy: three real clients behind it
    [InlineData(false, 1)]  // untrusted: X-Forwarded-For ignored, all clicks are the proxy itself
    public async Task UniqueVisitors_BehindProxy_OnlyTrustedForwardedHeadersCount(bool trusted, int expectedUnique)
    {
        var factory = _factory.WithWebHostBuilder(b =>
        {
            if (trusted)
            {
                b.UseSetting("Network:TrustedNetworks:0", "10.0.0.0/8");
            }

            // Simulate every request arriving from a reverse proxy at 10.0.0.5.
            b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(new FixedRemoteIp("10.0.0.5")));
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        var code = (await (await client.CreateLinkAsync(new { url = $"https://example.com/proxy-{trusted}" }))
            .Content.ReadFromJsonAsync<LinkResponse>(Json))!.Code;

        foreach (var clientIp in new[] { "203.0.113.1", "203.0.113.2", "203.0.113.3" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/{code}");
            request.Headers.Add("X-Forwarded-For", clientIp);
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Found);
        }

        var stats = await Eventually.GetAsync(
            () => client.GetFromJsonAsync<LinkStatsResponse>($"/api/v1/links/{code}/stats", Json),
            s => s!.TotalClicks >= 3);
        stats!.UniqueVisitors.Should().Be(expectedUnique);
    }

    [Fact]
    public async Task ExpectedConflicts_DoNotProduceErrorLogs()
    {
        // Scenario 2 measured 40 Error-level EF Core log lines for 20 handled 409s: false alerts in Splunk.
        var captured = new CapturingLoggerProvider();
        var client = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(captured)))
            .CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        await client.CreateLinkAsync(new { url = "https://example.com/1", customAlias = "noisy-alias" });
        for (var i = 0; i < 5; i++)
        {
            (await client.CreateLinkAsync(new { url = "https://example.com/2", customAlias = "noisy-alias" }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        captured.Entries.Where(e => e.Level >= LogLevel.Error).Should().BeEmpty();
    }

    private sealed class FixedRemoteIp(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
