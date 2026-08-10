using Aggregator.Catalog.Contracts;
using Aggregator.Query.Application;

namespace Query.Application.Tests;

public sealed class CatalogPublicationSeoProjectionBuilderTests
{
    private static readonly DateTimeOffset PublishedAtUtc =
        new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid PublicationId =
        Guid.Parse("01990f50-0000-7000-8000-000000000001");
    private static readonly Guid ConfigurationRevisionId =
        Guid.Parse("01990f50-0000-7000-8000-000000000002");
    private static readonly Guid ListingId =
        Guid.Parse("01990f50-0000-7000-8000-000000000003");

    [Fact]
    public void CatalogRouteManifestOwnsListingRouteAndSeoRedirectProjection()
    {
        const string currentPath = "/de-DE/studios/exact-studio";
        const string legacyPath = "/de-DE/studios/legacy-studio";
        var routeGroup = CatalogPublicationRouteManifest.ListingGroupKey(ListingId);
        var artifact = CreateArtifact() with
        {
            Routes =
            [
                new PublicRouteDocument(
                    CatalogPublicRouteKindContract.Listing,
                    routeGroup,
                    "de-DE",
                    currentPath,
                    PublishedAtUtc,
                    IsDraft: false,
                    IsSuppressed: false),
            ],
            Redirects =
            [
                new PublicRouteRedirect(
                    CatalogPublicRouteKindContract.Listing,
                    routeGroup,
                    "de-DE",
                    legacyPath,
                    currentPath,
                    PublicationId,
                    "listing slug changed",
                    PublishedAtUtc),
            ],
        };

        var projection = CatalogPublicationProjectionBuilder.Build(
            CreateActivation(),
            artifact,
            Guid.Parse("01990f50-0000-7000-8000-000000000010"),
            Guid.Parse("01990f50-0000-7000-8000-000000000011"),
            Guid.Parse("01990f50-0000-7000-8000-000000000012"),
            Guid.Parse("01990f50-0000-7000-8000-000000000013"),
            PublishedAtUtc.AddMinutes(1));

        Assert.Equal(
            currentPath,
            Assert.Single(Assert.Single(projection.BaseProjection.Documents).Localizations).RoutePath);
        Assert.Equal(currentPath, Assert.Single(projection.SeoProjection.Records).Path.Value);
        var redirect = Assert.Single(projection.SeoProjection.Redirects);
        Assert.Equal(legacyPath, redirect.SourcePath.Value);
        Assert.Equal(currentPath, redirect.TargetPath.Value);
        Assert.Equal(
            projection.PublicReadRevision.Id,
            projection.SeoProjection.PublicReadRevisionId);
    }

    [Fact]
    public void ObservedListingLocaleWithoutCurrentRouteIsRejected()
    {
        var artifact = CreateArtifact() with
        {
            Routes = Array.Empty<PublicRouteDocument>(),
        };

        var exception = Assert.Throws<QueryProjectionException>(() =>
            CatalogPublicationProjectionBuilder.Build(
                CreateActivation(),
                artifact,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                PublishedAtUtc.AddMinutes(1)));

        Assert.Equal("QUERY_ROUTE_LISTING_MISSING", exception.Code);
    }

    private static CatalogPublicationActivated CreateActivation() =>
        new(
            Guid.Parse("01990f50-0000-7000-8000-000000000020"),
            PublicationId,
            "recording-services",
            ConfigurationRevisionId,
            1,
            1,
            "catalog/publications/sealed/exact.json",
            new string('a', 64),
            PublicationActivationKindContract.Publication,
            null,
            PublishedAtUtc);

    private static CatalogPublicationArtifact CreateArtifact() =>
        new(
            CatalogPublicationArtifactContract.Identity,
            CatalogPublicationArtifactContract.Revision,
            PublicationId,
            "recording-services",
            "de-DE",
            ["de-DE"],
            ConfigurationRevisionId,
            1,
            PublishedAtUtc,
            [
                new PublicListingDocument(
                    ListingId,
                    Guid.Parse("01990f50-0000-7000-8000-000000000004"),
                    Guid.Parse("01990f50-0000-7000-8000-000000000005"),
                    Guid.Parse("01990f50-0000-7000-8000-000000000006"),
                    SubjectKindContract.Place,
                    [
                        new PublicLocalizedText(
                            "de-DE",
                            FieldValueStateContract.Observed,
                            "Exact Studio",
                            null,
                            Guid.Parse("01990f50-0000-7000-8000-000000000007")),
                    ],
                    [],
                    ["recording-studio"],
                    [],
                    new PublicGeography(
                        GeographyStateContract.PrimaryMarket,
                        52.5m,
                        13.4m,
                        "mitte",
                        Guid.Parse("01990f50-0000-7000-8000-000000000008")),
                    [],
                    [],
                    [],
                    new string('b', 64)),
            ]);
}
