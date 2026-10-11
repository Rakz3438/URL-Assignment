using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Api.Contracts;
using UrlShortener.Application.Abstractions;
using UrlShortener.Application.Common;
using UrlShortener.Domain.Links;
using UrlShortener.IntegrationTests.Infrastructure;

namespace UrlShortener.IntegrationTests;

/// <summary>Scenario 3, R1: database outage. Cached links keep redirecting; everything else answers
/// 503 + Retry-After instead of a raw 500.</summary>
public class ResilienceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ResilienceTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task DatabaseDown_CachedLinkRedirects_UncachedReturns503WithRetryAfter()
    {
        var outage = new OutageSwitch();
        var client = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                var original = s.Single(d => d.ServiceType == typeof(IShortLinkRepository));
                s.Remove(original);
                s.AddScoped<IShortLinkRepository>(sp => new OutageAwareRepository(
                    (IShortLinkRepository)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), outage));
            }))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        var cached = await CreateAsync(client, "https://example.com/cached");   // write-through: now in cache
        outage.Down = true;

        var hit = await client.GetAsync($"/{cached}");
        var miss = await client.GetAsync("/notcached1");
        var create = await client.CreateLinkAsync(new { url = "https://example.com/during-outage" });

        hit.StatusCode.Should().Be(HttpStatusCode.Found, "cached links survive a database outage");
        miss.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        miss.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        (await miss.Content.ReadAsStringAsync()).Should().Contain("dependency_unavailable").And.NotContain("Simulated");
        create.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        outage.Down = false;
        (await client.GetAsync($"/{cached}")).StatusCode.Should().Be(HttpStatusCode.Found);
    }

    private static async Task<string> CreateAsync(HttpClient client, string url)
    {
        var response = await client.CreateLinkAsync(new { url });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!.Code;
    }

    private sealed class OutageSwitch
    {
        public volatile bool Down;
    }

    private sealed class OutageAwareRepository(IShortLinkRepository inner, OutageSwitch outage) : IShortLinkRepository
    {
        public Task<ShortLink?> GetByCodeAsync(string code, CancellationToken ct) => Guard(() => inner.GetByCodeAsync(code, ct));
        public Task<ShortLink?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) => Guard(() => inner.GetByIdempotencyKeyAsync(key, ct));
        public Task<bool> TryAddAsync(ShortLink link, CancellationToken ct) => Guard(() => inner.TryAddAsync(link, ct));
        public Task UpdateAsync(ShortLink link, CancellationToken ct) => Guard(async () => { await inner.UpdateAsync(link, ct); return true; });

        private Task<T> Guard<T>(Func<Task<T>> call) => outage.Down
            ? throw new DependencyUnavailableException("Database", new TimeoutException("Simulated database outage"))
            : call();
    }
}
