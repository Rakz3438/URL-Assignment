namespace UrlShortener.Domain.Analytics;

/// <summary>Pre-aggregated click count for one link on one UTC day (read model, URL-11).
/// Maintained by click ingestion with an atomic upsert, so stats no longer scan raw clicks.</summary>
public sealed class LinkDailyStat
{
    // Required by EF Core materialization.
    private LinkDailyStat()
    {
        Code = string.Empty;
    }

    public LinkDailyStat(string code, DateOnly day, long clicks)
    {
        Code = code;
        Day = day;
        Clicks = clicks;
    }

    public string Code { get; private set; }
    public DateOnly Day { get; private set; }
    public long Clicks { get; private set; }
}
