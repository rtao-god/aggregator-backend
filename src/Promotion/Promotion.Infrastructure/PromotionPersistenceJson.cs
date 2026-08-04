using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;

namespace Aggregator.Promotion.Infrastructure;

internal static class PromotionPersistenceJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static string SerializeStringDictionary(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return JsonSerializer.Serialize(
            values.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            SerializerOptions);
    }

    public static IReadOnlyDictionary<string, string> DeserializeStringDictionary(string json) =>
        Deserialize<Dictionary<string, string>>(json, "Promotion string dictionary");

    public static string SerializeEnumSet<TEnum>(IEnumerable<TEnum> values)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(values);
        return JsonSerializer.Serialize(
            values.Distinct().OrderBy(value => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            SerializerOptions);
    }

    public static IReadOnlyList<TEnum> DeserializeEnumSet<TEnum>(string json)
        where TEnum : struct, Enum =>
        Deserialize<TEnum[]>(json, $"Promotion {typeof(TEnum).Name} set");

    public static string SerializeStringSet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return JsonSerializer.Serialize(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), SerializerOptions);
    }

    public static IReadOnlyList<string> DeserializeStringSet(string json) =>
        Deserialize<string[]>(json, "Promotion string set");

    public static (string Kind, string Json, string Digest) SerializeResult<TAggregate>(TAggregate aggregate)
        where TAggregate : class
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        object snapshot = aggregate switch
        {
            PromotionProduct product => ProductSnapshot.From(product),
            PromotionEntitlement entitlement => EntitlementSnapshot.From(entitlement),
            SponsoredPlacement placement => PlacementSnapshot.From(placement),
            _ => throw new PromotionApplicationException(
                "Promotion.Persistence",
                "PROMOTION_RESULT_KIND_UNSUPPORTED",
                500,
                $"Promotion command result type '{typeof(TAggregate).FullName}' is unsupported.",
                "Add an explicit immutable persistence snapshot for this Promotion owner result."),
        };
        var kind = snapshot switch
        {
            ProductSnapshot => "product",
            EntitlementSnapshot => "entitlement",
            PlacementSnapshot => "placement",
            _ => throw new InvalidOperationException("Promotion persistence snapshot kind is unsupported."),
        };
        var json = JsonSerializer.Serialize(snapshot, snapshot.GetType(), SerializerOptions);
        return (kind, json, PromotionCanonicalJson.ComputeDigest(snapshot));
    }

    public static TAggregate DeserializeResult<TAggregate>(
        string kind,
        string json,
        string expectedDigest)
        where TAggregate : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDigest);
        object snapshot = kind switch
        {
            "product" => Deserialize<ProductSnapshot>(json, "Promotion product command result"),
            "entitlement" => Deserialize<EntitlementSnapshot>(json, "Promotion entitlement command result"),
            "placement" => Deserialize<PlacementSnapshot>(json, "Promotion placement command result"),
            _ => throw new PromotionApplicationException(
                "Promotion.Persistence",
                "PROMOTION_RESULT_KIND_UNSUPPORTED",
                500,
                $"Stored Promotion result kind '{kind}' is unsupported.",
                "Repair or remove the corrupt command result through an owner migration."),
        };
        var actualDigest = PromotionCanonicalJson.ComputeDigest(snapshot);
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw new PromotionApplicationException(
                "Promotion.Persistence",
                "PROMOTION_RESULT_DIGEST_MISMATCH",
                500,
                "Stored Promotion command result digest does not match its immutable result document.",
                "Block replay and restore the command result from a verified database backup.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["resultKind"] = kind,
                    ["expectedDigest"] = expectedDigest,
                    ["actualDigest"] = actualDigest,
                });
        }

        object aggregate = snapshot switch
        {
            ProductSnapshot product => product.Restore(),
            EntitlementSnapshot entitlement => entitlement.Restore(),
            PlacementSnapshot placement => placement.Restore(),
            _ => throw new InvalidOperationException("Promotion persistence snapshot kind is unsupported."),
        };
        return aggregate as TAggregate
            ?? throw new PromotionApplicationException(
                "Promotion.Persistence",
                "PROMOTION_RESULT_TYPE_MISMATCH",
                500,
                $"Stored Promotion result kind '{kind}' cannot be returned as '{typeof(TAggregate).Name}'.",
                "Correct the command-result owner mapping before replaying the command.");
    }

    private static T Deserialize<T>(string json, string ownerDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new PromotionApplicationException(
                    "Promotion.Persistence",
                    "PROMOTION_PERSISTED_JSON_EMPTY",
                    500,
                    $"{ownerDescription} deserialized to no value.",
                    "Restore the exact persisted document from a verified database backup.");
        }
        catch (JsonException exception)
        {
            throw new PromotionApplicationException(
                "Promotion.Persistence",
                "PROMOTION_PERSISTED_JSON_INVALID",
                500,
                $"{ownerDescription} is not valid under the current persistence contract.",
                "Run the owner migration or restore a compatible database backup.",
                innerException: exception);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record ProductRevisionSnapshot(
        Guid Id,
        Guid ProductId,
        long RevisionNumber,
        IReadOnlyDictionary<string, string> DisplayNames,
        IReadOnlyList<PromotionPresentationFeature> PresentationFeatures,
        bool RequiresVerifiedContact,
        string? RequiredContactCapability,
        Guid CreatedByActorId,
        DateTimeOffset CreatedAtUtc,
        string ContentDigest)
    {
        public static ProductRevisionSnapshot From(PromotionProductRevision revision) =>
            new(
                revision.Id,
                revision.ProductId,
                revision.RevisionNumber,
                revision.DisplayNames,
                revision.PresentationFeatures.OrderBy(feature => (int)feature).ToArray(),
                revision.RequiresVerifiedContact,
                revision.RequiredContactCapability,
                revision.CreatedByActorId,
                revision.CreatedAtUtc,
                revision.ContentDigest);

        public PromotionProductRevision Restore() =>
            PromotionProductRevision.Create(
                Id,
                ProductId,
                RevisionNumber,
                DisplayNames,
                PresentationFeatures,
                RequiresVerifiedContact,
                RequiredContactCapability,
                CreatedByActorId,
                CreatedAtUtc,
                ContentDigest);
    }

    private sealed record ProductSnapshot(
        Guid Id,
        string Key,
        PromotionProductState State,
        ProductRevisionSnapshot CurrentRevision,
        long AggregateRevision)
    {
        public static ProductSnapshot From(PromotionProduct product) =>
            new(
                product.Id,
                product.Key,
                product.State,
                ProductRevisionSnapshot.From(product.CurrentRevision),
                product.AggregateRevision);

        public PromotionProduct Restore() =>
            PromotionProduct.Restore(
                Id,
                Key,
                State,
                CurrentRevision.Restore(),
                AggregateRevision);
    }

    private sealed record EntitlementSnapshot(
        Guid Id,
        Guid ListingId,
        string ProductKey,
        PromotionEntitlementSourceType SourceType,
        string ExternalReference,
        DateTimeOffset StartsAtUtc,
        DateTimeOffset EndsAtUtc,
        PromotionEntitlementState State,
        Guid CreatedByActorId,
        string AuditReason,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ChangedAtUtc,
        long AggregateRevision)
    {
        public static EntitlementSnapshot From(PromotionEntitlement entitlement) =>
            new(
                entitlement.Id,
                entitlement.ListingId,
                entitlement.ProductKey,
                entitlement.SourceType,
                entitlement.ExternalReference,
                entitlement.EffectiveWindow.StartsAtUtc,
                entitlement.EffectiveWindow.EndsAtUtc,
                entitlement.State,
                entitlement.CreatedByActorId,
                entitlement.AuditReason,
                entitlement.CreatedAtUtc,
                entitlement.ChangedAtUtc,
                entitlement.AggregateRevision);

        public PromotionEntitlement Restore() =>
            PromotionEntitlement.Restore(
                Id,
                ListingId,
                ProductKey,
                SourceType,
                ExternalReference,
                PromotionWindow.Create(StartsAtUtc, EndsAtUtc),
                State,
                CreatedByActorId,
                AuditReason,
                CreatedAtUtc,
                ChangedAtUtc,
                AggregateRevision);
    }

    private sealed record PlacementRevisionSnapshot(
        Guid Id,
        Guid PlacementId,
        long RevisionNumber,
        string CatalogKey,
        PlacementScopeType ScopeType,
        string ScopeKey,
        IReadOnlyList<string> LocaleScope,
        DateTimeOffset StartsAtUtc,
        DateTimeOffset EndsAtUtc,
        int PriorityBand,
        int CapacitySlot,
        string PresentationLabelKey,
        Guid CreatedByActorId,
        DateTimeOffset CreatedAtUtc,
        string ContentDigest)
    {
        public static PlacementRevisionSnapshot From(SponsoredPlacementRevision revision) =>
            new(
                revision.Id,
                revision.PlacementId,
                revision.RevisionNumber,
                revision.CatalogKey,
                revision.ScopeType,
                revision.ScopeKey,
                revision.LocaleScope.Order(StringComparer.Ordinal).ToArray(),
                revision.EffectiveWindow.StartsAtUtc,
                revision.EffectiveWindow.EndsAtUtc,
                revision.PriorityBand,
                revision.CapacitySlot,
                revision.PresentationLabelKey,
                revision.CreatedByActorId,
                revision.CreatedAtUtc,
                revision.ContentDigest);

        public SponsoredPlacementRevision Restore() =>
            SponsoredPlacementRevision.Create(
                Id,
                PlacementId,
                RevisionNumber,
                CatalogKey,
                ScopeType,
                ScopeKey,
                LocaleScope,
                PromotionWindow.Create(StartsAtUtc, EndsAtUtc),
                PriorityBand,
                CapacitySlot,
                PresentationLabelKey,
                CreatedByActorId,
                CreatedAtUtc,
                ContentDigest);
    }

    private sealed record PlacementSnapshot(
        Guid Id,
        Guid EntitlementId,
        Guid ListingId,
        string ProductKey,
        SponsoredPlacementState State,
        PlacementRevisionSnapshot CurrentRevision,
        DateTimeOffset ChangedAtUtc,
        string AuditReason,
        long AggregateRevision)
    {
        public static PlacementSnapshot From(SponsoredPlacement placement) =>
            new(
                placement.Id,
                placement.EntitlementId,
                placement.ListingId,
                placement.ProductKey,
                placement.State,
                PlacementRevisionSnapshot.From(placement.CurrentRevision),
                placement.ChangedAtUtc,
                placement.AuditReason,
                placement.AggregateRevision);

        public SponsoredPlacement Restore() =>
            SponsoredPlacement.Restore(
                Id,
                EntitlementId,
                ListingId,
                ProductKey,
                State,
                CurrentRevision.Restore(),
                ChangedAtUtc,
                AuditReason,
                AggregateRevision);
    }
}
