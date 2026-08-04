namespace Aggregator.Promotion.Domain;

/// <summary>Defines one exact UTC half-open promotion interval <c>[StartsAtUtc, EndsAtUtc)</c>.</summary>
public sealed record PromotionWindow
{
    private PromotionWindow(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
    }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset EndsAtUtc { get; }

    public static PromotionWindow Create(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        PromotionDomainRules.RequireUtc(startsAtUtc, nameof(startsAtUtc));
        PromotionDomainRules.RequireUtc(endsAtUtc, nameof(endsAtUtc));
        if (endsAtUtc <= startsAtUtc)
        {
            throw new PromotionDomainException(
                "PROMOTION_WINDOW_INVALID",
                "Promotion window end must be later than its start.");
        }

        return new PromotionWindow(startsAtUtc, endsAtUtc);
    }

    public bool Contains(DateTimeOffset timestampUtc)
    {
        PromotionDomainRules.RequireUtc(timestampUtc, nameof(timestampUtc));
        return timestampUtc >= StartsAtUtc && timestampUtc < EndsAtUtc;
    }

    public bool Contains(PromotionWindow other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.StartsAtUtc >= StartsAtUtc && other.EndsAtUtc <= EndsAtUtc;
    }

    public bool Overlaps(PromotionWindow other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return StartsAtUtc < other.EndsAtUtc && other.StartsAtUtc < EndsAtUtc;
    }
}
