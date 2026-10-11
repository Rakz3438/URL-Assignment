using System.Collections.Concurrent;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain.Links;

namespace UrlShortener.UnitTests.Fakes;

/// <summary>In-memory repository that enforces the same unique constraints as the database.</summary>
internal sealed class InMemoryLinkRepository : IShortLinkRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ShortLink> _byCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortLink> _byKey = new(StringComparer.Ordinal);

    public int AddAttempts { get; private set; }
    public int Reads { get; private set; }

    public IReadOnlyCollection<ShortLink> All
    {
        get
        {
            lock (_gate)
            {
                return _byCode.Values.ToList();
            }
        }
    }

    public Task<ShortLink?> GetByCodeAsync(string code, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Reads++;
            return Task.FromResult(_byCode.GetValueOrDefault(code));
        }
    }

    public Task<ShortLink?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_byKey.GetValueOrDefault(idempotencyKey));
        }
    }

    public Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            AddAttempts++;
            if (_byCode.ContainsKey(link.Code) || (link.IdempotencyKey is not null && _byKey.ContainsKey(link.IdempotencyKey)))
            {
                return Task.FromResult(false);
            }

            _byCode[link.Code] = link;
            if (link.IdempotencyKey is not null)
            {
                _byKey[link.IdempotencyKey] = link;
            }

            return Task.FromResult(true);
        }
    }

    public Task UpdateAsync(ShortLink link, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _byCode[link.Code] = link;
            return Task.CompletedTask;
        }
    }
}

/// <summary>Returns a scripted sequence of codes so collision handling can be tested deterministically.</summary>
internal sealed class ScriptedCodeGenerator : IShortCodeGenerator
{
    private readonly ConcurrentQueue<string> _codes;

    public ScriptedCodeGenerator(params string[] codes) => _codes = new ConcurrentQueue<string>(codes);

    public string Generate() => _codes.TryDequeue(out var code) ? code : throw new InvalidOperationException("Script exhausted.");
}

/// <summary>Dictionary-backed <see cref="ILinkCache"/> that records calls.</summary>
internal sealed class InMemoryLinkCache : ILinkCache
{
    private readonly ConcurrentDictionary<string, LinkSnapshot> _entries = new(StringComparer.Ordinal);

    public int Gets { get; private set; }
    public IReadOnlyDictionary<string, LinkSnapshot> Entries => _entries;

    public Task<LinkSnapshot?> GetAsync(string code, CancellationToken cancellationToken)
    {
        Gets++;
        return Task.FromResult(_entries.GetValueOrDefault(code));
    }

    public Task SetAsync(LinkSnapshot link, CancellationToken cancellationToken)
    {
        _entries[link.Code] = link;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string code, CancellationToken cancellationToken)
    {
        _entries.TryRemove(code, out _);
        return Task.CompletedTask;
    }
}

internal sealed class CollectingClickSink : IClickSink
{
    public ConcurrentQueue<UrlShortener.Domain.Analytics.ClickEvent> Clicks { get; } = new();

    public bool TryEnqueue(UrlShortener.Domain.Analytics.ClickEvent click)
    {
        Clicks.Enqueue(click);
        return true;
    }
}

internal sealed class TestMeterFactory : System.Diagnostics.Metrics.IMeterFactory
{
    private readonly List<System.Diagnostics.Metrics.Meter> _meters = [];

    public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options)
    {
        var meter = new System.Diagnostics.Metrics.Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose() => _meters.ForEach(m => m.Dispose());
}
