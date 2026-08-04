namespace Aggregator.Catalog.Domain;

public readonly record struct SiteId(Guid Value)
{
    public static SiteId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct CatalogId(Guid Value)
{
    public static CatalogId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct OrganizationId(Guid Value)
{
    public static OrganizationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct PlaceId(Guid Value)
{
    public static PlaceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ProviderId(Guid Value)
{
    public static ProviderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct SubjectRevisionId(Guid Value)
{
    public static SubjectRevisionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ListingId(Guid Value)
{
    public static ListingId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ListingRevisionId(Guid Value)
{
    public static ListingRevisionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ProductConfigurationRevisionId(Guid Value)
{
    public static ProductConfigurationRevisionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct TaxonomyRevisionId(Guid Value)
{
    public static TaxonomyRevisionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct AttributeRevisionId(Guid Value)
{
    public static AttributeRevisionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MarketAreaRevisionId(Guid Value)
{
    public static MarketAreaRevisionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct PublicationRequestId(Guid Value)
{
    public static PublicationRequestId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct PublicationId(Guid Value)
{
    public static PublicationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ActorId(Guid Value)
{
    public static ActorId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
