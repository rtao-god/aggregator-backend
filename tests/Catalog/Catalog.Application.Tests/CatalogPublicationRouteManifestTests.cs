using Aggregator.Catalog.Contracts;

namespace Catalog.Application.Tests;

public sealed class CatalogPublicationRouteManifestTests
{
    [Fact]
    public void LegacyConstructorDerivesExactObservedListingRoutes()
    {
        var timestamp = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);
        var listingId = Guid.Parse("01990f40-0000-7000-8000-000000000010");
        var artifact = new CatalogPublicationArtifact(
            CatalogPublicationArtifactContract.Identity,
            CatalogPublicationArtifactContract.Revision,
            Guid.Parse("01990f40-0000-7000-8000-000000000011"),
            "recording-services",
            "de-DE",
            ["de-DE", "en-GB"],
            Guid.Parse("01990f40-0000-7000-8000-000000000012"),
            1,
            timestamp,
            [CreateListing(listingId)]);

        Assert.Equal(2, artifact.Routes.Count);
        Assert.Equal(
            ["de-DE", "en-GB"],
            artifact.Routes.Select(route => route.Locale));
        Assert.All(
            artifact.Routes,
            route =>
            {
                Assert.Equal(CatalogPublicRouteKindContract.Listing, route.RouteKind);
                Assert.Equal($"listing:{listingId:D}", route.RouteGroupKey);
                Assert.Equal($"/{route.Locale}/listings/{listingId:N}", route.Path);
                Assert.Equal(timestamp, route.LastModifiedAtUtc);
                Assert.False(route.IsDraft);
                Assert.False(route.IsSuppressed);
            });
        Assert.Empty(artifact.Redirects);
    }

    private static PublicListingDocument CreateListing(Guid listingId) =>
        new(
            listingId,
            Guid.Parse("01990f40-0000-7000-8000-000000000013"),
            Guid.Parse("01990f40-0000-7000-8000-000000000014"),
            Guid.Parse("01990f40-0000-7000-8000-000000000015"),
            SubjectKindContract.Place,
            [
                new PublicLocalizedText(
                    "en-GB",
                    FieldValueStateContract.Observed,
                    "Exact Studio",
                    null,
                    Guid.Parse("01990f40-0000-7000-8000-000000000016")),
                new PublicLocalizedText(
                    "de-DE",
                    FieldValueStateContract.Observed,
                    "Exaktes Studio",
                    null,
                    Guid.Parse("01990f40-0000-7000-8000-000000000017")),
                new PublicLocalizedText(
                    "fr-FR",
                    FieldValueStateContract.Missing,
                    null,
                    MissingValueReasonContract.NotCollected,
                    null),
            ],
            [],
            ["recording-studio"],
            [],
            new PublicGeography(
                GeographyStateContract.PrimaryMarket,
                52.5m,
                13.4m,
                "mitte",
                Guid.Parse("01990f40-0000-7000-8000-000000000018")),
            [],
            [],
            [],
            new string('a', 64));
}
