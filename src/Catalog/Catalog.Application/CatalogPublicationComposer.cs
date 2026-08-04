using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public static class CatalogPublicationComposer
{
    public const string SchemaIdentity = "aggregator-catalog-publication/1";

    public static CatalogPublicationBundleDto Compose(
        CatalogPublicationRequest request,
        IReadOnlyList<PublicationListingSource> sources,
        string generatorBuild)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sources);
        if (string.IsNullOrWhiteSpace(generatorBuild) || generatorBuild.Length > 200)
        {
            throw new CatalogCommandException(
                "Catalog.Publication",
                "GENERATOR_BUILD_INVALID",
                500,
                "Publication generator build identity is missing or invalid.",
                "Deploy the worker with one exact generator build identity.");
        }

        if (sources.Count != request.SelectedListings.Length)
        {
            throw CoverageMismatch(request.SelectedListings.Length, sources.Count);
        }

        var selected = request.SelectedListings.ToDictionary(item => item.ListingId, item => item.ListingRevisionId);
        var seen = new HashSet<ListingId>();
        var listings = new List<CatalogPublicationListingDto>(sources.Count);
        var routes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources.OrderBy(item => item.Listing.Id.Value))
        {
            if (!selected.TryGetValue(source.Listing.Id, out var selectedRevisionId)
                || selectedRevisionId != source.Revision.Id
                || source.Revision.ListingId != source.Listing.Id
                || !seen.Add(source.Listing.Id))
            {
                throw new CatalogCommandException(
                    "Catalog.Publication",
                    "PUBLICATION_SELECTION_MISMATCH",
                    409,
                    "Loaded listing sources do not match the exact publication selection.",
                    "Rebuild the publication work unit from the persisted exact request.");
            }

            var revision = CatalogContractMapper.ToDto(source.Revision);
            listings.Add(new CatalogPublicationListingDto(
                source.Listing.Id.Value,
                source.Revision.Id.Value,
                source.Listing.Subject.Kind == ListingKind.Place
                    ? ListingKindContract.Place
                    : ListingKindContract.Provider,
                source.Listing.Subject.SubjectId,
                revision.Translations,
                revision.CategoryKeys,
                revision.Attributes,
                revision.ContentDigest));
            foreach (var translation in source.Revision.Translations)
            {
                routes.Add(
                    $"{translation.Locale}:{source.Listing.Id.Value:D}",
                    $"/listings/{source.Listing.Id.Value:D}");
            }
        }

        if (seen.Count != selected.Count)
        {
            throw CoverageMismatch(selected.Count, seen.Count);
        }

        var listingIndex = listings
            .Select(item => new ListingIndexItem(item.ListingId, item.ListingRevisionId, item.ContentDigest))
            .ToArray();
        var listingIndexDigest = CatalogCanonicalJson.ComputeDigest(listingIndex);
        var routeManifestDigest = CatalogCanonicalJson.ComputeDigest(routes);
        return new CatalogPublicationBundleDto(
            SchemaIdentity,
            request.PublicationId.Value,
            request.CatalogId.Value,
            request.ExpectedCurrentPublicationId?.Value,
            request.ProductConfigurationRevisionId.Value,
            request.TaxonomyRevisionId.Value,
            request.AttributeRevisionId.Value,
            request.MarketAreaRevisionId.Value,
            request.RequestedAtUtc,
            generatorBuild,
            listings.Count,
            listingIndexDigest,
            routeManifestDigest,
            listings,
            routes,
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<string>());
    }

    private static CatalogCommandException CoverageMismatch(int expected, int actual) =>
        new(
            "Catalog.Publication",
            "PUBLICATION_COVERAGE_MISMATCH",
            409,
            $"Publication expected {expected} listings but materialized {actual}.",
            "Repair the persisted publication work selection; do not skip missing listings.");

    private sealed record ListingIndexItem(Guid ListingId, Guid ListingRevisionId, string ContentDigest);
}
