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
