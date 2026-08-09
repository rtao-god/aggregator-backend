using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Contracts;

namespace Aggregator.Ingestion.Application;

/// <summary>Disposition of one producer-owned Catalog configuration event in the Ingestion projection.</summary>
public enum CatalogConfigurationProjectionDisposition
{
    Applied = 1,
    Replayed = 2,
}

/// <summary>Validated Ingestion-local projection of one exact active Catalog configuration.</summary>
public sealed record CatalogConfigurationProjection(
    string SiteKey,
    string CatalogKey,
    Guid ConfigurationRevisionId,
    Guid? PreviousConfigurationRevisionId,
    string ConfigurationDigest,
    string MarketAreaKey,
    IReadOnlyList<IngestionEntityKindContract> SupportedListingKinds,
    long AggregateRevision,
    Guid SourceEventId,
    string SourcePayloadDigest,
    DateTimeOffset ActivatedAtUtc,
    string ProjectionDigest);

/// <summary>Owns the canonical digest of the Ingestion-local Catalog configuration projection.</summary>
public static class CatalogConfigurationProjectionDigest
{
    public static string Compute(
        string siteKey,
        string catalogKey,
        Guid configurationRevisionId,
        Guid? previousConfigurationRevisionId,
        string configurationDigest,
        string marketAreaKey,
        IReadOnlyList<IngestionEntityKindContract> supportedListingKinds,
        long aggregateRevision,
        Guid sourceEventId,
        string sourcePayloadDigest,
        DateTimeOffset activatedAtUtc) =>
        IngestionCanonicalJson.ComputeDigest(
            new DigestDocument(
                siteKey,
                catalogKey,
                configurationRevisionId,
                previousConfigurationRevisionId,
                configurationDigest,
                marketAreaKey,
                supportedListingKinds,
                aggregateRevision,
                sourceEventId,
                sourcePayloadDigest,
                activatedAtUtc));

    private sealed record DigestDocument(
        string SiteKey,
        string CatalogKey,
        Guid ConfigurationRevisionId,
        Guid? PreviousConfigurationRevisionId,
        string ConfigurationDigest,
        string MarketAreaKey,
        IReadOnlyList<IngestionEntityKindContract> SupportedListingKinds,
        long AggregateRevision,
        Guid SourceEventId,
        string SourcePayloadDigest,
        DateTimeOffset ActivatedAtUtc);
}

/// <summary>Broker metadata retained by the Ingestion inbox for one Catalog configuration event.</summary>
public sealed record CatalogConfigurationInboxMessage(
    Guid EventId,
    string RoutingKey,
    string ContractIdentity,
    string PayloadDigest,
    string CorrelationId,
    DateTimeOffset ReceivedAtUtc);

public sealed record CatalogConfigurationProjectionResult(
    CatalogConfigurationProjection Projection,
    CatalogConfigurationProjectionDisposition Disposition);

/// <summary>Atomically owns Catalog-event inbox state and the Ingestion-local configuration projection.</summary>
public interface ICatalogConfigurationProjectionStore
{
    public Task<CatalogConfigurationProjectionResult> ApplyAsync(
        CatalogConfigurationProjection projection,
        CatalogConfigurationInboxMessage inboxMessage,
        CancellationToken cancellationToken);
}

/// <summary>Validates producer-owned Catalog configuration events before local projection persistence.</summary>
public sealed class ApplyCatalogConfigurationActivationService(
    ICatalogConfigurationProjectionStore store,
    TimeProvider timeProvider)
{
    public Task<CatalogConfigurationProjectionResult> ApplyAsync(
        CatalogConfigurationActivated activation,
        string payloadDigest,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        var projection = CreateProjection(activation, payloadDigest);
        var receivedAtUtc = RequireUtc(timeProvider.GetUtcNow(), "received timestamp");
        var inboxMessage = new CatalogConfigurationInboxMessage(
            activation.EventId,
            CatalogIntegrationEventTypes.ConfigurationActivated,
            CatalogIntegrationEventContracts.ConfigurationActivated,
            RequireDigest(payloadDigest, "payload digest"),
            RequireCorrelationId(correlationId),
            receivedAtUtc);
        return store.ApplyAsync(projection, inboxMessage, cancellationToken);
    }

    private static CatalogConfigurationProjection CreateProjection(
        CatalogConfigurationActivated activation,
        string payloadDigest)
    {
        if (activation.EventId == Guid.Empty)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_EVENT_ID_INVALID",
                422,
                "Catalog configuration activation event ID is empty.",
                "Republish the activation through the Catalog owner outbox.");
        }

        if (activation.ConfigurationRevisionId == Guid.Empty ||
            activation.PreviousConfigurationRevisionId == Guid.Empty)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_IDENTITY_INVALID",
                422,
                "Catalog configuration activation contains an empty configuration identity.",
                "Correct the Catalog activation event before replay.");
        }

        var siteKey = RequireKey(activation.SiteKey, "site key");
        var catalogKey = RequireKey(activation.CatalogKey, "catalog key");
        var marketAreaKey = RequireKey(activation.MarketAreaKey, "market-area key");
        var configurationDigest = RequireDigest(
            activation.ConfigurationDigest,
            "configuration digest");
        var exactPayloadDigest = RequireDigest(payloadDigest, "payload digest");
        var activatedAtUtc = RequireUtc(activation.OccurredAtUtc, "activation timestamp");
        if (activation.AggregateRevision <= 0 ||
            (activation.AggregateRevision == 1 && activation.PreviousConfigurationRevisionId is not null) ||
            (activation.AggregateRevision > 1 && activation.PreviousConfigurationRevisionId is null))
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_REVISION_INVALID",
                422,
                "Catalog configuration activation revision and previous pointer are inconsistent.",
                "Correct the producer activation revision or replay the complete Catalog stream.",
                catalogKey,
                activation.AggregateRevision);
        }

        ArgumentNullException.ThrowIfNull(activation.SupportedListingKinds);
        if (activation.SupportedListingKinds.Count == 0)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_LISTING_KINDS_EMPTY",
                422,
                "Catalog configuration activation has no supported public listing kinds.",
                "Activate a Catalog configuration with at least one supported public listing kind.",
                catalogKey,
                activation.AggregateRevision);
        }

        var listingKinds = new List<IngestionEntityKindContract>(activation.SupportedListingKinds.Count);
        var previousNumericKind = 0;
        foreach (var producerKind in activation.SupportedListingKinds)
        {
            var numericKind = (int)producerKind;
            if (numericKind <= previousNumericKind)
            {
                throw Failure(
                    "INGESTION_CATALOG_CONFIGURATION_LISTING_KINDS_NOT_CANONICAL",
                    422,
                    "Catalog configuration listing kinds are duplicated or not in canonical order.",
                    "Republish the producer event with unique listing kinds ordered by wire identity.",
                    catalogKey,
                    activation.AggregateRevision);
            }

            listingKinds.Add(producerKind switch
            {
                SubjectKindContract.Place => IngestionEntityKindContract.Place,
                SubjectKindContract.Provider => IngestionEntityKindContract.Provider,
                _ => throw Failure(
                    "INGESTION_CATALOG_CONFIGURATION_LISTING_KIND_UNSUPPORTED",
                    422,
                    $"Catalog listing kind '{producerKind}' is not a public Ingestion listing kind.",
                    "Correct the active Catalog configuration before replay.",
                    catalogKey,
                    activation.AggregateRevision),
            });
            previousNumericKind = numericKind;
        }

        return new CatalogConfigurationProjection(
            siteKey,
            catalogKey,
            activation.ConfigurationRevisionId,
            activation.PreviousConfigurationRevisionId,
            configurationDigest,
            marketAreaKey,
            listingKinds.AsReadOnly(),
            activation.AggregateRevision,
            activation.EventId,
            exactPayloadDigest,
            activatedAtUtc,
            CatalogConfigurationProjectionDigest.Compute(
                siteKey,
                catalogKey,
                activation.ConfigurationRevisionId,
                activation.PreviousConfigurationRevisionId,
                configurationDigest,
                marketAreaKey,
                listingKinds,
                activation.AggregateRevision,
                activation.EventId,
                exactPayloadDigest,
                activatedAtUtc));
    }

    private static string RequireKey(string value, string meaning)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 96 ||
            value[0] is < 'a' or > 'z' ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
            value[^1] == '-' ||
            value.Contains("--", StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_KEY_INVALID",
                422,
                $"Catalog configuration {meaning} is invalid.",
                "Correct the producer-owned Catalog configuration event before replay.");
        }

        return value;
    }

    private static string RequireDigest(string value, string meaning)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_DIGEST_INVALID",
                422,
                $"Catalog configuration {meaning} is not a canonical SHA-256 digest.",
                "Correct the producer event or broker metadata before replay.");
        }

        return value;
    }

    private static string RequireCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length is < 8 or > 128 ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_CORRELATION_INVALID",
                422,
                "Catalog configuration event correlation identity is invalid.",
                "Correct the broker envelope before replay.");
        }

        return value;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string meaning)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_TIMESTAMP_NOT_UTC",
                422,
                $"Catalog configuration {meaning} is not UTC.",
                "Correct the producer event before replay.");
        }

        return value;
    }

    private static IngestionApplicationException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        string? catalogKey = null,
        long? aggregateRevision = null) =>
        new(
            "Ingestion.CatalogProjection",
            code,
            statusCode,
            detail,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = catalogKey,
                ["aggregateRevision"] = aggregateRevision,
            });
}
