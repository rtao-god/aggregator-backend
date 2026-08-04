using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aggregator.Analytics.Contracts;

namespace Aggregator.Analytics.Application;

public sealed record AnalyticsRuntimeOptions
{
    public required byte[] SessionHashKey { get; init; }

    public TimeSpan MaximumFutureSkew { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaximumEventAge { get; init; } = TimeSpan.FromDays(90);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(SessionHashKey);
        if (SessionHashKey.Length < 32)
        {
            throw new InvalidOperationException("Analytics session hash key must contain at least 32 bytes.");
        }

        if (MaximumFutureSkew < TimeSpan.Zero || MaximumFutureSkew > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("Analytics maximum future skew must be between zero and one hour.");
        }

        if (MaximumEventAge < TimeSpan.FromDays(1) || MaximumEventAge > TimeSpan.FromDays(366))
        {
            throw new InvalidOperationException("Analytics maximum event age must be between one and 366 days.");
        }
    }
}

public sealed record AnalyticsInteractionRecord(
    Guid EventId,
    string RequestDigest,
    string CatalogKey,
    Guid PublicReadRevisionId,
    Guid? ListingId,
    string SessionHash,
    AnalyticsInteractionKindContract Kind,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc);

public sealed record AnalyticsInteractionRegistration(
    DateTimeOffset RecordedAtUtc,
    bool Replayed);

public sealed record AnalyticsListingMetricsSnapshot(
    string CatalogKey,
    Guid ListingId,
    long ListingViews,
    long ContactClicks,
    long Leads,
    DateTimeOffset UpdatedAtUtc);

public interface IAnalyticsRuntimeStore
{
    public Task<AnalyticsInteractionRegistration> RegisterAsync(
        AnalyticsInteractionRecord interaction,
        CancellationToken cancellationToken);

    public Task<AnalyticsListingMetricsSnapshot?> ReadListingMetricsAsync(
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken);

    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken);
}

public sealed class AnalyticsRuntimeException : InvalidOperationException
{
    public AnalyticsRuntimeException(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        Code = code;
        StatusCode = statusCode;
        RequiredAction = requiredAction;
        Context = context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Code { get; }

    public int StatusCode { get; }

    public string RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }
}

public sealed class AnalyticsRuntimeService(
    IAnalyticsRuntimeStore store,
    AnalyticsRuntimeOptions options,
    TimeProvider timeProvider)
{
    private static readonly Regex CatalogKeyPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public async Task<AnalyticsInteractionReceipt> RecordAsync(
        RecordAnalyticsInteractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        options.Validate();
        ValidateRequest(request);
        var now = timeProvider.GetUtcNow();
        if (request.OccurredAtUtc > now + options.MaximumFutureSkew)
        {
            throw Failure(
                "ANALYTICS_EVENT_FROM_FUTURE",
                422,
                "Interaction timestamp exceeds the accepted future skew.",
                "Correct the collector clock and submit a new exact event.");
        }

        if (request.OccurredAtUtc < now - options.MaximumEventAge)
        {
            throw Failure(
                "ANALYTICS_EVENT_TOO_OLD",
                422,
                "Interaction timestamp is older than the accepted retention window.",
                "Do not replay interaction events outside the configured retention window.");
        }

        var normalizedCatalogKey = request.CatalogKey.Trim();
        var normalizedSessionKey = request.SessionKey.Trim();
        var requestDigest = ComputeRequestDigest(request with
        {
            CatalogKey = normalizedCatalogKey,
            SessionKey = normalizedSessionKey,
        });
        var sessionHash = ComputeSessionHash(normalizedSessionKey, options.SessionHashKey);
        var interaction = new AnalyticsInteractionRecord(
            request.EventId,
            requestDigest,
            normalizedCatalogKey,
            request.PublicReadRevisionId,
            request.ListingId,
            sessionHash,
            request.Kind,
            request.OccurredAtUtc,
            now);
        var registration = await store.RegisterAsync(interaction, cancellationToken);
        return new AnalyticsInteractionReceipt(
            request.EventId,
            registration.RecordedAtUtc,
            registration.Replayed);
    }

    public async Task<AnalyticsListingMetricsResponse> ReadListingMetricsAsync(
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        var normalizedCatalogKey = RequireCatalogKey(catalogKey);
        if (listingId == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_LISTING_ID_INVALID",
                400,
                "Listing ID is required.",
                "Provide a non-empty listing ID.");
        }

        var snapshot = await store.ReadListingMetricsAsync(
            normalizedCatalogKey,
            listingId,
            cancellationToken);
        if (snapshot is null)
        {
            return new AnalyticsListingMetricsResponse(
                normalizedCatalogKey,
                listingId,
                0,
                0,
                0,
                timeProvider.GetUtcNow());
        }

        return new AnalyticsListingMetricsResponse(
            snapshot.CatalogKey,
            snapshot.ListingId,
            snapshot.ListingViews,
            snapshot.ContactClicks,
            snapshot.Leads,
            snapshot.UpdatedAtUtc);
    }

    private static void ValidateRequest(RecordAnalyticsInteractionRequest request)
    {
        if (request.EventId == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_EVENT_ID_INVALID",
                400,
                "Interaction event ID is required.",
                "Generate one UUIDv7 event ID per interaction attempt.");
        }

        _ = RequireCatalogKey(request.CatalogKey);
        if (request.PublicReadRevisionId == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_PUBLIC_READ_REVISION_INVALID",
                400,
                "Public read revision ID is required.",
                "Submit the exact revision rendered to the visitor.");
        }

        if (string.IsNullOrWhiteSpace(request.SessionKey) || request.SessionKey.Trim().Length > 256)
        {
            throw Failure(
                "ANALYTICS_SESSION_KEY_INVALID",
                400,
                "Session key must contain between one and 256 characters.",
                "Submit an opaque bounded session key; do not submit personal data.");
        }

        if (!Enum.IsDefined(request.Kind))
        {
            throw Failure(
                "ANALYTICS_INTERACTION_KIND_INVALID",
                400,
                "Interaction kind is unsupported.",
                "Submit one of the declared Analytics interaction kinds.");
        }

        if (request.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_TIMESTAMP_NOT_UTC",
                400,
                "Interaction timestamp must use UTC.",
                "Normalize the collector timestamp to UTC.");
        }

        var requiresListing = request.Kind is
            AnalyticsInteractionKindContract.ListingView or
            AnalyticsInteractionKindContract.ContactClick or
            AnalyticsInteractionKindContract.Lead;
        if (requiresListing && request.ListingId is null or { } id && id == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_LISTING_ID_REQUIRED",
                400,
                "This interaction kind requires a non-empty listing ID.",
                "Submit the exact listing rendered or contacted.");
        }

        if (!requiresListing && request.ListingId == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_LISTING_ID_INVALID",
                400,
                "Listing ID cannot be an empty GUID.",
                "Omit the listing ID for a page view or submit a non-empty ID.");
        }
    }

    private static string RequireCatalogKey(string catalogKey)
    {
        if (string.IsNullOrWhiteSpace(catalogKey))
        {
            throw Failure(
                "ANALYTICS_CATALOG_KEY_REQUIRED",
                400,
                "Catalog key is required.",
                "Submit the exact public catalog key.");
        }

        var normalized = catalogKey.Trim();
        if (normalized.Length > 96 || !CatalogKeyPattern.IsMatch(normalized))
        {
            throw Failure(
                "ANALYTICS_CATALOG_KEY_INVALID",
                400,
                "Catalog key is not a normalized lower-case identifier.",
                "Submit a lower-case hyphen-separated catalog key.");
        }

        return normalized;
    }

    private static string ComputeRequestDigest(RecordAnalyticsInteractionRequest request)
    {
        var canonical = string.Join(
            '\n',
            request.EventId.ToString("D"),
            request.CatalogKey,
            request.PublicReadRevisionId.ToString("D"),
            request.ListingId?.ToString("D") ?? string.Empty,
            request.SessionKey,
            ((int)request.Kind).ToString(CultureInfo.InvariantCulture),
            request.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeSessionHash(string sessionKey, ReadOnlySpan<byte> key)
    {
        using var hmac = new HMACSHA256(key.ToArray());
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(sessionKey)));
    }

    private static AnalyticsRuntimeException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null) =>
        new(code, statusCode, message, requiredAction, context, innerException);
}
