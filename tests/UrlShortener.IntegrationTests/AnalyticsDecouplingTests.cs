using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Api.Contracts;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain.Analytics;
using UrlShortener.IntegrationTests.Infrastructure;

namespace UrlShortener.IntegrationTests;

/// <summary>Brownfield scenario (URL-10/URL-11/URL-13): non-critical dependencies must never break the redirect.
/// The first test was written failing-first against the synchronous baseline (it returned 500).</summary>
public class AnalyticsDecouplingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AnalyticsDecouplingTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Redirect_AnalyticsStoreFailing_StillRedirects()
    {
        var client = CreateClient(s =>
        {
            s.RemoveAll<IClickIngestor>();
            s.AddScoped<IClickIngestor, FailingIngestor>();
        });
        var code = await CreateLinkAsync(client, "https://example.com/decoupled");

        var response = await client.GetAsync($"/{code}");

        response.StatusCode.Should().Be(HttpStatusCode.Found, "analytics is non-critical and must not fail the redirect");
    }

    [Fact]
    public async Task Redirect_AnalyticsStoreHanging_RedirectIsNotDelayed()
    {
        var client = CreateClient(s =>
        {
            s.RemoveAll<IClickIngestor>();
            s.AddScoped<IClickIngestor, HangingIngestor>();
        });
        var code = await CreateLinkAsync(client, "https://example.com/hanging");
        await client.GetAsync($"/{code}"); // warm-up

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            (await client.GetAsync($"/{code}")).StatusCode.Should().Be(HttpStatusCode.Found);
        }

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the 10 s analytics stall from the baseline must not reach users");
    }

    [Fact]
    public async Task Redirect_CacheUnavailable_FallsBackToDatabase()
    {
        var client = CreateClient(s =>
        {
            s.RemoveAll<IDistributedCache>();
            s.AddSingleton<IDistributedCache, FailingCache>();
        });
        var code = await CreateLinkAsync(client, "https://example.com/no-cache");

        var response = await client.GetAsync($"/{code}");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be("https://example.com/no-cache");
    }

    [Fact]
    public async Task Redirect_AfterDelete_CachedEntryInvalidated()
    {
        var client = _factory.CreateAuthorizedClient();
        var code = await CreateLinkAsync(client, "https://example.com/cached-then-deleted");
        (await client.GetAsync($"/{code}")).StatusCode.Should().Be(HttpStatusCode.Found); // now definitely cached

        await client.DeleteAsync($"/api/v1/links/{code}");

        (await client.GetAsync($"/{code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private HttpClient CreateClient(Action<IServiceCollection> configure)
    {
        var client = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(configure))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }

    private static async Task<string> CreateLinkAsync(HttpClient client, string url)
    {
        var response = await client.CreateLinkAsync(new { url });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!.Code;
    }

    private sealed class FailingIngestor : IClickIngestor
    {
        public Task<int> IngestAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated analytics store outage.");
    }

    private sealed class HangingIngestor : IClickIngestor
    {
        public async Task<int> IngestAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); // the baseline's 10 s lock
            return clicks.Count;
        }
    }

    private sealed class FailingCache : IDistributedCache
    {
        private static InvalidOperationException Down => new("Simulated Redis outage");
        public byte[]? Get(string key) => throw Down;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Down;
        public void Refresh(string key) => throw Down;
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Down;
        public void Remove(string key) => throw Down;
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Down;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Down;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Down;
    }
}
