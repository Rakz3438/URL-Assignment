using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Application.Common;
using UrlShortener.Domain.Links;

namespace UrlShortener.Application.Links;

public sealed record CreateLinkCommand(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt, string? IdempotencyKey);

/// <param name="Replayed">True when an earlier result was returned for a repeated Idempotency-Key.</param>
public sealed record CreateLinkResult(ShortLink Link, bool Replayed);

/// <summary>Use cases for managing short links (FR-1, FR-3, FR-4, FR-5, FR-6, FR-8).</summary>
public sealed partial class LinkService
{
    public const int MaxIdempotencyKeyLength = 128;

    private readonly IShortLinkRepository _repository;
    private readonly IShortCodeGenerator _codeGenerator;
    private readonly UrlPolicy _urlPolicy;
    private readonly TimeProvider _clock;
    private readonly ShortenerOptions _options;
    private readonly ILogger<LinkService> _logger;

    public LinkService(IShortLinkRepository repository, IShortCodeGenerator codeGenerator, UrlPolicy urlPolicy,
        TimeProvider clock, IOptions<ShortenerOptions> options, ILogger<LinkService> logger)
    {
        _repository = repository;
        _codeGenerator = codeGenerator;
        _urlPolicy = urlPolicy;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<CreateLinkResult>> CreateAsync(CreateLinkCommand command, CancellationToken cancellationToken)
    {
        var urlResult = _urlPolicy.Validate(command.Url);
        if (!urlResult.IsSuccess)
        {
            return urlResult.Error!;
        }

        var targetUrl = urlResult.Value;
        var now = _clock.GetUtcNow();

        if (command.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return Error.Validation("expiresAt.past", "expiresAt must be in the future.");
        }

        // Aliases are case-insensitive by storing them lowercase: prevents lookalike pairs such as
        // "Promo" and "promo" pointing to different destinations (engineer decision, traceability note 1).
        command = command with { CustomAlias = command.CustomAlias?.ToLowerInvariant() };

        if (command.CustomAlias is not null && !ShortCode.IsValidAlias(command.CustomAlias, out var aliasError))
        {
            return Error.Validation("alias.invalid", aliasError!);
        }

        string? requestHash = null;
        if (command.IdempotencyKey is not null)
        {
            if (command.IdempotencyKey.Length is 0 or > MaxIdempotencyKeyLength)
            {
                return Error.Validation("idempotencyKey.invalid", $"Idempotency-Key must be 1-{MaxIdempotencyKeyLength} characters.");
            }

            requestHash = HashRequest(targetUrl, command.CustomAlias, command.ExpiresAt);
            var replay = await TryReplayAsync(command.IdempotencyKey, requestHash, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }
        }

        var attempts = command.CustomAlias is null ? _options.MaxCodeGenerationAttempts : 1;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var code = command.CustomAlias ?? _codeGenerator.Generate();
            if (command.CustomAlias is null && ShortCode.IsReserved(code))
            {
                // Would be shadowed by a service route; treat like a collision and draw again.
                LogCollision(_logger, attempt);
                continue;
            }

            var link = ShortLink.Create(code, targetUrl, command.CustomAlias is not null, now, command.ExpiresAt,
                command.IdempotencyKey, requestHash);

            if (await _repository.TryAddAsync(link, cancellationToken))
            {
                LogCreated(_logger, link.Code, link.IsCustomAlias);
                return new CreateLinkResult(link, Replayed: false);
            }

            // A unique constraint fired. Either a concurrent request with the same Idempotency-Key won the race...
            if (command.IdempotencyKey is not null)
            {
                var replay = await TryReplayAsync(command.IdempotencyKey, requestHash!, cancellationToken);
                if (replay is not null)
                {
                    return replay;
                }
            }

            // ...or the code itself is taken.
            if (command.CustomAlias is not null)
            {
                return Error.Conflict("alias.taken", "The requested alias is already in use.");
            }

            LogCollision(_logger, attempt);
        }

        // Astronomically unlikely with 62^7 codes; if it happens the keyspace is filling up and needs attention.
        throw new InvalidOperationException($"Could not generate a unique code after {attempts} attempts.");
    }

    public async Task<Result<ShortLink>> GetAsync(string code, CancellationToken cancellationToken)
    {
        var link = ShortCode.IsWellFormed(code) ? await _repository.GetByCodeAsync(code, cancellationToken) : null;
        return link is null || link.GetStatus(_clock.GetUtcNow()) == LinkStatus.Deleted
            ? Error.NotFound()
            : link;
    }

    public async Task<Result<bool>> DeleteAsync(string code, CancellationToken cancellationToken)
    {
        var link = ShortCode.IsWellFormed(code) ? await _repository.GetByCodeAsync(code, cancellationToken) : null;
        if (link is null || link.GetStatus(_clock.GetUtcNow()) == LinkStatus.Deleted)
        {
            return Error.NotFound();
        }

        link.Delete(_clock.GetUtcNow());
        await _repository.UpdateAsync(link, cancellationToken);
        LogDeleted(_logger, code);
        return true;
    }

    private async Task<Result<CreateLinkResult>?> TryReplayAsync(string key, string requestHash, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetByIdempotencyKeyAsync(key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        return existing.RequestHash == requestHash
            ? new CreateLinkResult(existing, Replayed: true)
            : Error.Unprocessable("idempotencyKey.reused", "Idempotency-Key was already used with a different request body.");
    }

    internal static string HashRequest(string targetUrl, string? alias, DateTimeOffset? expiresAt)
    {
        var canonical = string.Join('\n', targetUrl, alias ?? string.Empty,
            expiresAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created short link {Code} (customAlias={IsCustomAlias})")]
    private static partial void LogCreated(ILogger logger, string code, bool isCustomAlias);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Short code collision on attempt {Attempt}; retrying")]
    private static partial void LogCollision(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted short link {Code}")]
    private static partial void LogDeleted(ILogger logger, string code);
}
