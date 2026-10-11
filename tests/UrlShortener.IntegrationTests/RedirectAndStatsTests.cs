using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.IntegrationTests.Infrastructure;

namespace UrlShortener.IntegrationTests;

public class RedirectAndStatsTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public RedirectAndStatsTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthorizedClient();
    }

    [Fact]
    public async Task Redirect_ActiveLink_Returns302NoStoreWithoutApiKey()
    {
        var link = await CreateAsync(new { url = "https://example.com/target?x=1" });

        var response = await _factory.CreateAnonymousClient().GetAsync($"/{link.Code}");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be("https://example.com/target?x=1");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Redirect_IdnTarget_LocationHeaderIsAscii()
    {
        var link = await CreateAsync(new { url = "https://bücher.example/straße" });

        var response = await _client.GetAsync($"/{link.Code}");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.Should().Be("https://xn--bcher-kva.example/stra%C3%9Fe");
    }

    [Fact]
    public async Task Redirect_AfterExpiry_Returns410()
    {
        var link = await CreateAsync(new { url = "https://example.com/expiring", expiresAt = _factory.Clock.GetUtcNow().AddHours(1) });
        (await _client.GetAsync($"/{link.Code}")).StatusCode.Should().Be(HttpStatusCode.Found);

        _factory.Clock.Advance(TimeSpan.FromHours(2));

        var response = await _client.GetAsync($"/{link.Code}");
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await _client.GetFromJsonAsync<LinkResponse>($"/api/v1/links/{link.Code}", Json))!.Status.Should().Be("expired");
    }

    [Fact]
    public async Task Redirect_UnknownCode_Returns404()
    {
        (await _client.GetAsync("/doesnotexist")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stats_AfterClicks_ReturnsTotalsReferrersAgentsAndDailySeries()
    {
        // Regression test: this endpoint returned 500 in the first smoke test (untranslatable LINQ GroupBy).
        var link = await CreateAsync(new { url = "https://example.com/stats" });
        await ClickAsync(link.Code, "https://twitter.com/post/1", "Mozilla/5.0 Chrome/120.0 Safari/537.36");
        await ClickAsync(link.Code, "https://twitter.com/post/2", "Mozilla/5.0 Chrome/120.0 Safari/537.36");
        await ClickAsync(link.Code, "https://news.example.org/", "Mozilla/5.0 Firefox/121.0");
        await ClickAsync(link.Code, null, "curl/8.4.0");

        // Clicks flow through the background pipeline (URL-11): wait for ingestion, bounded by a deadline.
        var stats = await Eventually.GetAsync(
            () => _client.GetFromJsonAsync<LinkStatsResponse>($"/api/v1/links/{link.Code}/stats", Json),
            s => s!.TotalClicks >= 4);

        stats!.TotalClicks.Should().Be(4);
        stats.TopReferrers.Should().HaveCount(2);
        stats.TopReferrers[0].Name.Should().Be("twitter.com");
        stats.TopReferrers[0].Count.Should().Be(2);
        stats.TopUserAgents.Should().Contain(x => x.Name == "Chrome" && x.Count == 2);
        stats.Daily.Should().ContainSingle().Which.Clicks.Should().Be(4);
    }

    [Fact]
    public async Task Stats_DeletedLink_Returns404()
    {
        var link = await CreateAsync(new { url = "https://example.com/stats-deleted" });
        await _client.DeleteAsync($"/api/v1/links/{link.Code}");

        (await _client.GetAsync($"/api/v1/links/{link.Code}/stats")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task ClickAsync(string code, string? referrer, string userAgent)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{code}");
        if (referrer is not null)
        {
            request.Headers.Referrer = new Uri(referrer);
        }

        request.Headers.UserAgent.ParseAdd(userAgent);
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Found);
    }

    private async Task<LinkResponse> CreateAsync(object body)
    {
        var response = await _client.CreateLinkAsync(body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkResponse>(Json))!;
    }
}
