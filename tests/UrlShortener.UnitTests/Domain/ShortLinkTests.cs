using UrlShortener.Domain.Common;
using UrlShortener.Domain.Links;

namespace UrlShortener.UnitTests.Domain;

public class ShortLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidInput_IsActive()
    {
        var link = ShortLink.Create("abc1234", "https://example.com/", false, Now, Now.AddDays(1));

        link.GetStatus(Now).Should().Be(LinkStatus.Active);
        link.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_ExpiryNotInFuture_Throws()
    {
        var act = () => ShortLink.Create("abc1234", "https://example.com/", false, Now, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_NonUtcOffsets_StoredAsUtc()
    {
        var ist = new DateTimeOffset(2026, 10, 11, 17, 30, 0, TimeSpan.FromHours(5.5)); // 12:00 UTC
        var link = ShortLink.Create("abc1234", "https://example.com/", false, ist, ist.AddHours(1));

        link.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        link.CreatedAt.Should().Be(Now);
        link.ExpiresAt!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void GetStatus_AtExactExpiry_IsExpired()
    {
        var link = ShortLink.Create("abc1234", "https://example.com/", false, Now, Now.AddHours(1));

        link.GetStatus(Now.AddHours(1).AddTicks(-1)).Should().Be(LinkStatus.Active);
        link.GetStatus(Now.AddHours(1)).Should().Be(LinkStatus.Expired);
    }

    [Fact]
    public void Delete_CalledTwice_KeepsFirstTimestamp()
    {
        var link = ShortLink.Create("abc1234", "https://example.com/", false, Now, null);

        link.Delete(Now.AddMinutes(1));
        link.Delete(Now.AddMinutes(5));

        link.DeletedAt.Should().Be(Now.AddMinutes(1));
        link.GetStatus(Now.AddMinutes(10)).Should().Be(LinkStatus.Deleted);
    }

    [Fact]
    public void GetStatus_DeletedAndExpired_DeletedWins()
    {
        var link = ShortLink.Create("abc1234", "https://example.com/", false, Now, Now.AddMinutes(1));
        link.Delete(Now);

        link.GetStatus(Now.AddDays(1)).Should().Be(LinkStatus.Deleted);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("bad code")]
    public void Create_MalformedCode_Throws(string code)
    {
        var act = () => ShortLink.Create(code, "https://example.com/", false, Now, null);

        act.Should().Throw<DomainException>();
    }
}
