using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

/// <summary>One accepted Analytics interaction eligible for sponsored-usage derivation.</summary>
public sealed record AcceptedSponsoredInteraction(
    Guid EventId,
    InteractionEventKind EventKind,
    string CatalogKey,
    Guid ListingId,
    Guid PlacementId,
    DateTimeOffset OccurredAtUtc,
    string PayloadDigest);

/// <summary>Analytics-owned quality-filtered usage for one exact sponsored placement and UTC day.</summary>
public sealed record DerivedPromotionUsageWindow(
    Guid PlacementId,
    Guid ListingId,
    string CatalogKey,
    DateTimeOffset WindowStartsAtUtc,
    DateTimeOffset WindowEndsAtUtc,
    long AcceptedImpressions,
    long AcceptedListingOpens,
    long AcceptedOutboundClicks,
    string SourceDigest);

/// <summary>Derives deterministic Promotion usage only from accepted sponsored interactions.</summary>
public static class PromotionUsageWindowDeriver
{
    public static IReadOnlyList<DerivedPromotionUsageWindow> Derive(
        IReadOnlyList<AcceptedSponsoredInteraction> interactions,
        DateOnly fromInclusive,
        DateOnly toExclusive)
    {
        ArgumentNullException.ThrowIfNull(interactions);
        if (toExclusive <= fromInclusive)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_RANGE_INVALID",
                "Promotion usage derivation requires a positive [from, to) date range.");
        }

        var rangeStartsAtUtc = ToUtcStart(fromInclusive);
        var rangeEndsAtUtc = ToUtcStart(toExclusive);
        var normalized = interactions
            .Select(Validate)
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.EventId)
            .ToArray();
        foreach (var interaction in normalized)
        {
            if (interaction.OccurredAtUtc < rangeStartsAtUtc ||
                interaction.OccurredAtUtc >= rangeEndsAtUtc)
            {
                throw Failure(
                    "ANALYTICS_PROMOTION_USAGE_EVENT_OUTSIDE_RANGE",
                    $"Sponsored interaction '{interaction.EventId:D}' is outside the exact aggregation range.");
            }
        }

        var result = new List<DerivedPromotionUsageWindow>();
        foreach (var group in normalized
                     .GroupBy(item => new
                     {
                         Date = DateOnly.FromDateTime(item.OccurredAtUtc.UtcDateTime),
                         item.PlacementId,
                     })
                     .OrderBy(item => item.Key.Date)
                     .ThenBy(item => item.Key.PlacementId))
        {
            var listingIds = group.Select(item => item.ListingId).Distinct().ToArray();
            var catalogKeys = group
                .Select(item => item.CatalogKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (listingIds.Length != 1 || catalogKeys.Length != 1)
            {
                throw Failure(
                    "ANALYTICS_PROMOTION_USAGE_IDENTITY_DIVERGED",
                    $"Placement '{group.Key.PlacementId:D}' maps to multiple listing or Catalog identities in one usage window.");
            }

            long impressions = 0;
            long listingOpens = 0;
            long outboundClicks = 0;
            var relevant = new List<AcceptedSponsoredInteraction>();
            foreach (var interaction in group)
            {
                switch (interaction.EventKind)
                {
                    case InteractionEventKind.ListingImpression:
                        impressions++;
                        relevant.Add(interaction);
                        break;
                    case InteractionEventKind.ListingOpened:
                        listingOpens++;
                        relevant.Add(interaction);
                        break;
                    case InteractionEventKind.WebsiteClicked:
                    case InteractionEventKind.PhoneClicked:
                    case InteractionEventKind.WhatsAppClicked:
                    case InteractionEventKind.EmailClicked:
                    case InteractionEventKind.MapClicked:
                    case InteractionEventKind.ExternalProfileClicked:
                        outboundClicks++;
                        relevant.Add(interaction);
                        break;
                    case InteractionEventKind.SearchResultsViewed:
                    case InteractionEventKind.ClaimStarted:
                    case InteractionEventKind.ClaimSubmitted:
                        break;
                    default:
                        throw Failure(
                            "ANALYTICS_PROMOTION_USAGE_EVENT_KIND_UNSUPPORTED",
                            $"Sponsored interaction '{interaction.EventId:D}' contains unsupported kind '{interaction.EventKind}'.");
                }
            }

            if (relevant.Count == 0)
            {
                continue;
            }

            var startsAtUtc = ToUtcStart(group.Key.Date);
            result.Add(new DerivedPromotionUsageWindow(
                group.Key.PlacementId,
                listingIds[0],
                catalogKeys[0],
                startsAtUtc,
                startsAtUtc.AddDays(1),
                impressions,
                listingOpens,
                outboundClicks,
                ComputeSourceDigest(
                    startsAtUtc,
                    group.Key.PlacementId,
                    listingIds[0],
                    catalogKeys[0],
                    relevant)));
        }

        return result;
    }

    /// <summary>Creates an explicit complete zero revision for an existing sponsored usage window.</summary>
    public static DerivedPromotionUsageWindow CreateZeroCorrection(
        Guid placementId,
        Guid listingId,
        string catalogKey,
        DateTimeOffset windowStartsAtUtc,
        DateTimeOffset windowEndsAtUtc)
    {
        RequireIdentity(placementId, nameof(placementId));
        RequireIdentity(listingId, nameof(listingId));
        if (string.IsNullOrWhiteSpace(catalogKey) ||
            catalogKey.Length > 200 ||
            catalogKey.Any(char.IsControl) ||
            !string.Equals(catalogKey, catalogKey.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_CATALOG_INVALID",
                "Promotion usage zero correction has an invalid Catalog identity.");
        }

        if (windowStartsAtUtc.Offset != TimeSpan.Zero ||
            windowEndsAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_TIME_NOT_UTC",
                "Promotion usage zero-correction window must be UTC.");
        }

        if (windowEndsAtUtc <= windowStartsAtUtc ||
            windowEndsAtUtc != windowStartsAtUtc.AddDays(1))
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_WINDOW_INVALID",
                "Promotion usage zero correction requires one exact positive UTC day.");
        }

        return new DerivedPromotionUsageWindow(
            placementId,
            listingId,
            catalogKey,
            windowStartsAtUtc,
            windowEndsAtUtc,
            AcceptedImpressions: 0,
            AcceptedListingOpens: 0,
            AcceptedOutboundClicks: 0,
            ComputeSourceDigest(
                windowStartsAtUtc,
                placementId,
                listingId,
                catalogKey,
                []));
    }

    private static AcceptedSponsoredInteraction Validate(AcceptedSponsoredInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        RequireIdentity(interaction.EventId, nameof(interaction.EventId));
        RequireIdentity(interaction.ListingId, nameof(interaction.ListingId));
        RequireIdentity(interaction.PlacementId, nameof(interaction.PlacementId));
        if (!Enum.IsDefined(interaction.EventKind))
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_EVENT_KIND_UNSUPPORTED",
                $"Sponsored interaction '{interaction.EventId:D}' contains unsupported kind '{interaction.EventKind}'.");
        }

        if (string.IsNullOrWhiteSpace(interaction.CatalogKey) ||
            interaction.CatalogKey.Length > 200 ||
            interaction.CatalogKey.Any(char.IsControl) ||
            !string.Equals(
                interaction.CatalogKey,
                interaction.CatalogKey.Trim(),
                StringComparison.Ordinal))
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_CATALOG_INVALID",
                $"Sponsored interaction '{interaction.EventId:D}' has an invalid Catalog identity.");
        }

        if (interaction.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_TIME_NOT_UTC",
                $"Sponsored interaction '{interaction.EventId:D}' occurrence time must be UTC.");
        }

        if (interaction.PayloadDigest.Length != 64 ||
            interaction.PayloadDigest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_DIGEST_INVALID",
                $"Sponsored interaction '{interaction.EventId:D}' payload digest is invalid.");
        }

        return interaction;
    }

    private static string ComputeSourceDigest(
        DateTimeOffset startsAtUtc,
        Guid placementId,
        Guid listingId,
        string catalogKey,
        IReadOnlyList<AcceptedSponsoredInteraction> interactions)
    {
        var source = new StringBuilder();
        source.Append(startsAtUtc.ToString("O", CultureInfo.InvariantCulture))
            .Append('|')
            .Append(placementId.ToString("D"))
            .Append('|')
            .Append(listingId.ToString("D"))
            .Append('|')
            .Append(catalogKey)
            .Append('\n');
        foreach (var interaction in interactions
                     .OrderBy(item => item.OccurredAtUtc)
                     .ThenBy(item => item.EventId))
        {
            source.Append(interaction.EventId.ToString("D"))
                .Append('|')
                .Append(((int)interaction.EventKind).ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(interaction.PayloadDigest.ToLowerInvariant())
                .Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }

    private static void RequireIdentity(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_IDENTITY_INVALID",
                $"Promotion usage identity '{parameterName}' is required.");
        }
    }

    private static DateTimeOffset ToUtcStart(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private static AnalyticsCommandException Failure(string code, string detail) =>
        new(
            "Analytics.PromotionUsage",
            code,
            422,
            detail,
            "Rebuild the exact accepted sponsored interaction set before materializing Promotion usage.");
}
