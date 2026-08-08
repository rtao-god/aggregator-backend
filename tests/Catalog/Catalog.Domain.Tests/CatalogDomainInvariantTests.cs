using Aggregator.Catalog.Domain;

namespace Catalog.Domain.Tests;

public sealed class CatalogDomainInvariantTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrganizationCannotBePublicListingSubject()
    {
        var subject = SubjectReference.Create(Guid.NewGuid(), Guid.NewGuid(), SubjectKind.Organization);

        var exception = Assert.Throws<CatalogInvariantException>(() =>
            Listing.Create(Guid.NewGuid(), CatalogKey.Create("catalog"), subject, Timestamp));

        Assert.Contains("organization", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResearchOnlyAssertionCannotBackPublicListingContent()
    {
        var configuration = CreateConfiguration();
        var nameAssertion = Assertion(UsagePolicy.ResearchOnly);
        var categoryAssertion = Assertion(UsagePolicy.PublicAllowed);
        var geographyAssertion = Assertion(UsagePolicy.PublicAllowed);

        var exception = Assert.Throws<CatalogInvariantException>(() =>
            ListingRevisionContent.Create(
                SubjectKind.Place,
                [LocalizedTextValue.Observed(LocaleCode.Create("de-DE"), "Studio", nameAssertion.Id)],
                [],
                [CategoryAssignment.Create(CategoryKey.Create("recording-studio"), categoryAssertion.Id)],
                [],
                GeographyValue.Create(
                    GeographyState.PrimaryMarket,
                    52.52m,
                    13.40m,
                    "mitte",
                    geographyAssertion.Id),
                [],
                [],
                [nameAssertion, categoryAssertion, geographyAssertion],
                configuration));

        Assert.Contains("name:de-DE", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(UsagePolicy.ResearchOnly), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListingRejectsStaleAggregateVersion()
    {
        var configuration = CreateConfiguration();
        var listing = Listing.Create(
            Guid.NewGuid(),
            configuration.Catalog.Key,
            SubjectReference.Create(Guid.NewGuid(), Guid.NewGuid(), SubjectKind.Place),
            Timestamp);
        var content = CreatePublishableContent(configuration);
        _ = listing.AddDraftRevision(
            Guid.NewGuid(),
            expectedVersion: 1,
            configuration.RevisionId,
            listing.Subject,
            content,
            new string('a', 64),
            Guid.NewGuid(),
            Timestamp);

        var exception = Assert.Throws<CatalogConcurrencyException>(() =>
            listing.AddDraftRevision(
                Guid.NewGuid(),
                expectedVersion: 1,
                configuration.RevisionId,
                listing.Subject,
                content,
                new string('b', 64),
                Guid.NewGuid(),
                Timestamp));

        Assert.Equal(1, exception.ExpectedVersion);
        Assert.Equal(2, exception.ActualVersion);
    }

    [Fact]
    public void VerifiedClaimCreatesScopedGrantThatCanBeRevoked()
    {
        var claim = ListingClaim.Submit(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "private-evidence/reference",
            new string('c', 64),
            Timestamp);
        var reviewer = Guid.NewGuid();
        var grant = claim.Verify(
            Guid.NewGuid(),
            reviewer,
            [ListingAccessScope.ReadDraft, ListingAccessScope.ProposeRevision],
            Timestamp,
            Timestamp.AddDays(30));

        grant.EnsureScope(ListingAccessScope.ReadDraft, Timestamp.AddDays(1));
        _ = grant.Revoke(reviewer, "Owner access revoked.", Timestamp.AddDays(2));

        Assert.False(grant.IsActiveAt(Timestamp.AddDays(3)));
        Assert.Throws<CatalogAuthorizationException>(() =>
            grant.EnsureScope(ListingAccessScope.ReadDraft, Timestamp.AddDays(3)));
    }

    private static ProductConfiguration CreateConfiguration()
    {
        var siteKey = SiteKey.Create("site");
        var locale = LocaleCode.Create("de-DE");
        var categoryKey = CategoryKey.Create("recording-studio");
        return ProductConfiguration.Create(
            Guid.NewGuid(),
            new string('d', 64),
            SiteDefinition.Create(
                siteKey,
                locale,
                [locale],
                "EUR",
                "Europe/Berlin"),
            CatalogDefinition.Create(
                CatalogKey.Create("catalog"),
                siteKey,
                "berlin",
                "EUR",
                "Europe/Berlin",
                [SubjectKind.Place]),
            [
                CategoryDefinition.Create(
                    categoryKey,
                    [SubjectKind.Place],
                    new Dictionary<LocaleCode, string> { [locale] = "Tonstudio" },
                    isActive: true),
            ],
            [],
            Timestamp);
    }

    private static ListingRevisionContent CreatePublishableContent(ProductConfiguration configuration)
    {
        var nameAssertion = Assertion(UsagePolicy.PublicAllowed);
        var categoryAssertion = Assertion(UsagePolicy.PublicAllowed);
        var geographyAssertion = Assertion(UsagePolicy.PublicAllowed);
        return ListingRevisionContent.Create(
            SubjectKind.Place,
            [LocalizedTextValue.Observed(configuration.Site.DefaultLocale, "Studio", nameAssertion.Id)],
            [],
            [CategoryAssignment.Create(CategoryKey.Create("recording-studio"), categoryAssertion.Id)],
            [],
            GeographyValue.Create(
                GeographyState.PrimaryMarket,
                52.52m,
                13.40m,
                "mitte",
                geographyAssertion.Id),
            [],
            [],
            [nameAssertion, categoryAssertion, geographyAssertion],
            configuration);
    }

    private static ProvenanceAssertion Assertion(UsagePolicy policy) =>
        ProvenanceAssertion.Create(
            Guid.NewGuid(),
            SourceKind.FirstPartySubmission,
            "source",
            Timestamp,
            Timestamp,
            policy,
            new string('e', 64));
}
