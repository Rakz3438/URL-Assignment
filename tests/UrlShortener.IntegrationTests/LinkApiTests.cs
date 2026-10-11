using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.IntegrationTests.Infrastructure;

namespace UrlShortener.IntegrationTests;

public class LinkApiTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public LinkApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthorizedClient();
    }

    [Fact]
    public async Task Create_ValidUrl_Returns201WithLocationAndShortUrl()
    {
        var response = await _client.CreateLinkAsync(new { url = "https://example.com/create" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var link = await response.Content.ReadFromJsonAsync<LinkResponse>(Json);
        link!.Code.Should().MatchRegex("^[0-9A-Za-z]{7}$");
        link.ShortUrl.Should().Be($"{ApiFactory.PublicBaseUrl}/{link.Code}");
        link.Status.Should().Be("active");
        response.Headers.Location!.ToString().Should().Be($"/api/v1/links/{link.Code}");
    }

    [Fact]
    public async Task Get_ExistingLink_Returns200()
    {
        var created = await CreateAsync(new { url = "https://example.com/get", customAlias = "get-me" });

        var response = await _client.GetAsync($"/api/v1/links/{created.Code}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LinkResponse>(Json))!.TargetUrl.Should().Be("https://example.com/get");
    }

    [Theory]
    [InlineData("nope404x")]
    [InlineData("x")] // malformed: rejected before touching the database
    public async Task Get_UnknownOrMalformedCode_Returns404ProblemDetails(string code)
    {
        var response = await _client.GetAsync($"/api/v1/links/{code}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://sho.rt/loop")]
    public async Task Create_UnsafeUrl_Returns400(string url)
    {
        var response = await _client.CreateLinkAsync(new { url });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("url.invalid");
    }

    [Fact]
    public async Task Create_MixedCaseAlias_IsStoredLowercaseAndFollowable()
    {
        var created = await CreateAsync(new { url = "https://example.com/case", customAlias = "Spring-Sale" });

        created.Code.Should().Be("spring-sale");
        (await _client.GetAsync("/spring-sale")).StatusCode.Should().Be(HttpStatusCode.Found);
        (await _client.CreateLinkAsync(new { url = "https://evil.example/", customAlias = "SPRING-SALE" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_DuplicateAlias_Returns409()
    {
        await CreateAsync(new { url = "https://example.com/1", customAlias = "taken-alias" });

        var response = await _client.CreateLinkAsync(new { url = "https://example.com/2", customAlias = "taken-alias" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_ConcurrentSameAlias_ExactlyOneWinsRestConflict()
    {
        // Race condition guard: the DB unique index decides, never a 500.
        var tasks = Enumerable.Range(0, 20).Select(i =>
            _client.CreateLinkAsync(new { url = $"https://example.com/race/{i}", customAlias = "race-alias" }));

        var statuses = (await Task.WhenAll(tasks)).Select(r => r.StatusCode).ToList();

        statuses.Count(s => s == HttpStatusCode.Created).Should().Be(1);
        statuses.Count(s => s == HttpStatusCode.Conflict).Should().Be(19);
    }

    [Fact]
    public async Task Create_RetryWithIdempotencyKey_ReplaysIdentical201WithReplayHeader()
    {
        var body = new { url = "https://example.com/idem" };

        var first = await _client.CreateLinkAsync(body, "idem-key-1");
        var second = await _client.CreateLinkAsync(body, "idem-key-1");

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.GetValues("Idempotent-Replayed").Should().ContainSingle("true");
        second.Headers.Location.Should().Be(first.Headers.Location);
        (await second.Content.ReadFromJsonAsync<LinkResponse>(Json))!.Code
            .Should().Be((await first.Content.ReadFromJsonAsync<LinkResponse>(Json))!.Code);
    }

    [Fact]
    public async Task Create_IdempotencyKeyReusedWithDifferentBody_Returns422()
    {
        await _client.CreateLinkAsync(new { url = "https://example.com/a" }, "idem-key-2");

        var response = await _client.CreateLinkAsync(new { url = "https://example.com/b" }, "idem-key-2");

        response.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Delete_ExistingLink_Returns204AndLinkStopsResolving()
    {
        var created = await CreateAsync(new { url = "https://example.com/delete" });

        (await _client.DeleteAsync($"/api/v1/links/{created.Code}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/{created.Code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/v1/links/{created.Code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync($"/api/v1/links/{created.Code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ManagementEndpoints_NoOrWrongApiKey_Return401()
    {
        var anonymous = _factory.CreateAnonymousClient();
        var wrongKey = _factory.CreateAnonymousClient();
        wrongKey.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key-wrong-key-wrong-key");

        (await anonymous.CreateLinkAsync(new { url = "https://example.com" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrongKey.CreateLinkAsync(new { url = "https://example.com" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/links/abcdefg/stats")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OpenApi_Document_IsPublished()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await response.Content.ReadAsStringAsync();
        doc.Should().Contain("/api/v1/links").And.Contain("X-Api-Key");
    }

    private async Task<LinkResponse> CreateAsync(object body)
    {
        var response = await _client.CreateLinkAsync(body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkResponse>(Json))!;
    }
}
