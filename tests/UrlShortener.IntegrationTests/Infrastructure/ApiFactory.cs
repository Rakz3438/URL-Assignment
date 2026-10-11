using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace UrlShortener.IntegrationTests.Infrastructure;

/// <summary>Boots the real API (real HTTP pipeline, auth, EF Core, migrations) against an isolated
/// SQLite file per test class. Only the clock is replaced, so expiry can be tested deterministically.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-test-key-0123456789";
    public const string PublicBaseUrl = "https://sho.rt";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"urlshortener-test-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Links", $"Data Source={_dbPath}");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Auth:ApiKeys:0", ApiKey);
        builder.UseSetting("Shortener:PublicBaseUrl", PublicBaseUrl);
        builder.UseSetting("Shortener:VisitorHashSalt", "integration-test-salt-0123456789");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>Client that sends the API key and does NOT follow redirects (so 302s can be asserted).</summary>
    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    public HttpClient CreateAnonymousClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }
}

internal static class HttpExtensions
{
    public static Task<HttpResponseMessage> CreateLinkAsync(this HttpClient client, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/links") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }
}
