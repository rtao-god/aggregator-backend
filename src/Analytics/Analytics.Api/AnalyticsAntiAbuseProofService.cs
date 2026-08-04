using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Analytics.Application;
using Microsoft.Extensions.Configuration;

namespace Aggregator.Analytics.Api;

/// <summary>Requests one short-lived proof bound to an exact interaction identity and occurrence time.</summary>
public sealed record IssueAnalyticsAntiAbuseTokenRequest(
    Guid ClientEventId,
    DateTimeOffset OccurredAtUtc);

/// <summary>Returns one opaque proof that can be used only for the exact requested interaction.</summary>
public sealed record AnalyticsAntiAbuseTokenResponse(
    string Token,
    DateTimeOffset ExpiresAtUtc);

public sealed record AnalyticsAntiAbuseOptions
{
    public required ReadOnlyMemory<byte> SigningKey { get; init; }

    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromMinutes(2);

    public static AnalyticsAntiAbuseOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var encodedKey = configuration["Analytics:AntiAbuseSigningKey"];
        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new InvalidOperationException(
                "Analytics:AntiAbuseSigningKey is required.");
        }

        byte[] signingKey;
        try
        {
            signingKey = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Analytics:AntiAbuseSigningKey must be valid base64.",
                exception);
        }

        var lifetimeSeconds = configuration.GetValue<int?>(
            "Analytics:AntiAbuseTokenLifetimeSeconds") ?? 120;
        var options = new AnalyticsAntiAbuseOptions
        {
            SigningKey = signingKey,
            TokenLifetime = TimeSpan.FromSeconds(lifetimeSeconds),
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (SigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Analytics anti-abuse signing key must contain at least 32 bytes.");
        }

        if (TokenLifetime < TimeSpan.FromSeconds(30) ||
            TokenLifetime > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                "Analytics anti-abuse token lifetime must be between 30 seconds and five minutes.");
        }
    }
}

/// <summary>Issues and verifies rate-limited HMAC proofs without persisting client network identity.</summary>
public sealed class AnalyticsAntiAbuseProofService(
    AnalyticsAntiAbuseOptions options,
    TimeProvider timeProvider) : IAntiAbuseVerifier
{
    private const int DigestLength = 64;

    public AnalyticsAntiAbuseTokenResponse Issue(IssueAnalyticsAntiAbuseTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        options.Validate();
        if (request.ClientEventId == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_ANTI_ABUSE_EVENT_ID_INVALID",
                400,
                "Anti-abuse proof requires a non-empty client event ID.",
                "Generate one UUIDv7 client event ID and request a proof for that exact event.");
        }

        if (request.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_ANTI_ABUSE_TIMESTAMP_NOT_UTC",
                400,
                "Anti-abuse proof occurrence time must use UTC.",
                "Normalize the interaction occurrence time to UTC before requesting a proof.");
        }

        var now = timeProvider.GetUtcNow();
        if (request.OccurredAtUtc < now.AddDays(-7) ||
            request.OccurredAtUtc > now.AddMinutes(5))
        {
            throw Failure(
                "ANALYTICS_ANTI_ABUSE_TIMESTAMP_OUT_OF_BOUNDS",
                422,
                "Anti-abuse proof occurrence time is outside the accepted interaction window.",
                "Correct the client clock and request a proof for the exact current interaction.");
        }

        var expiresAtUtc = now.Add(options.TokenLifetime);
        var expiresUnixSeconds = expiresAtUtc.ToUnixTimeSeconds();
        var digest = ComputeDigest(
            request.ClientEventId,
            request.OccurredAtUtc,
            expiresUnixSeconds);
        return new AnalyticsAntiAbuseTokenResponse(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{expiresUnixSeconds}.{digest}"),
            expiresAtUtc);
    }

    public Task VerifyAsync(
        string antiAbuseToken,
        Guid clientEventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        if (string.IsNullOrWhiteSpace(antiAbuseToken) || antiAbuseToken.Length > 160)
        {
            throw Failure(
                "ANALYTICS_ANTI_ABUSE_TOKEN_INVALID",
                400,
                "Interaction anti-abuse token is missing or malformed.",
                "Request a fresh proof and submit it unchanged with the exact interaction.");
        }

        var separatorIndex = antiAbuseToken.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex == antiAbuseToken.Length - 1)
        {
            throw InvalidToken();
        }

        var expirationValue = antiAbuseToken[..separatorIndex];
        var suppliedDigest = antiAbuseToken[(separatorIndex + 1)..];
        if (!long.TryParse(
                expirationValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiresUnixSeconds) ||
            suppliedDigest.Length != DigestLength ||
            suppliedDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw InvalidToken();
        }

        var expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiresUnixSeconds);
        if (expiresAtUtc <= timeProvider.GetUtcNow())
        {
            throw Failure(
                "ANALYTICS_ANTI_ABUSE_TOKEN_EXPIRED",
                409,
                "Interaction anti-abuse token has expired.",
                "Request a fresh proof and resubmit the same semantic interaction.");
        }

        var expectedDigest = ComputeDigest(clientEventId, occurredAtUtc, expiresUnixSeconds);
        var suppliedBytes = Encoding.ASCII.GetBytes(suppliedDigest);
        var expectedBytes = Encoding.ASCII.GetBytes(expectedDigest);
        if (!CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        {
            throw InvalidToken();
        }

        return Task.CompletedTask;
    }

    private string ComputeDigest(
        Guid clientEventId,
        DateTimeOffset occurredAtUtc,
        long expiresUnixSeconds)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{clientEventId:D}\n{occurredAtUtc:O}\n{expiresUnixSeconds}");
        return Convert.ToHexStringLower(
            HMACSHA256.HashData(options.SigningKey.Span, Encoding.UTF8.GetBytes(canonical)));
    }

    private static AnalyticsCommandException InvalidToken() =>
        Failure(
            "ANALYTICS_ANTI_ABUSE_TOKEN_INVALID",
            400,
            "Interaction anti-abuse token does not match the exact event identity.",
            "Request a fresh proof and submit it unchanged with the exact interaction.");

    private static AnalyticsCommandException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction) =>
        new(
            "Analytics.AntiAbuse",
            code,
            statusCode,
            message,
            requiredAction);
}
