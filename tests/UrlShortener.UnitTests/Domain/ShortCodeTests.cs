using UrlShortener.Domain.Links;

namespace UrlShortener.UnitTests.Domain;

public class ShortCodeTests
{
    [Theory]
    [InlineData("abcd")]
    [InlineData("my-campaign_2026")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")] // exactly 32
    public void IsValidAlias_WellFormedAlias_ReturnsTrue(string alias)
    {
        ShortCode.IsValidAlias(alias, out var reason).Should().BeTrue();
        reason.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")] // too short
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456")] // 33, too long
    [InlineData("has space")]
    [InlineData("slash/path")]
    [InlineData("dot.dot")]
    [InlineData("ünïcode")]
    [InlineData("<script>")]
    public void IsValidAlias_MalformedAlias_ReturnsFalseWithReason(string? alias)
    {
        ShortCode.IsValidAlias(alias, out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("swagger")]
    [InlineData("HEALTH")]
    [InlineData("Admin")]
    public void IsValidAlias_ReservedWordAnyCase_ReturnsFalse(string alias)
    {
        ShortCode.IsValidAlias(alias, out var reason).Should().BeFalse();
        reason.Should().Contain("reserved");
    }

    [Theory]
    [InlineData("aB3dE6g", true)]
    [InlineData("abc", false)]
    [InlineData(null, false)]
    [InlineData("a%2Fb", false)]
    public void IsWellFormed_VariousInputs_MatchesRoutePattern(string? code, bool expected) =>
        ShortCode.IsWellFormed(code).Should().Be(expected);
}
