using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Ingestion.Application.Tests;

public sealed class IngestionPackageValidatorTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 4, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactPackageProducesAcceptedItemDecision()
    {
        var fixture = BuildPackage();

        var result = IngestionPackageValidator.ValidatePackage(
            fixture.Package,
            fixture.ManifestDigest);

        var item = Assert.Single(result.Items);
        Assert.Equal(ImportItemDecisionKind.Accepted, item.Decision);
        Assert.Equal("INGESTION_ITEM_CONTRACT_ACCEPTED", item.ReasonCode);
        Assert.Equal(fixture.Package.Manifest.PayloadDigest, result.PayloadDigest);
    }

    [Fact]
    public void DuplicateItemKeyRejectsWholePackageBeforePartialProcessing()
    {
        var first = CreateItem("candidate-1");
        var fixture = BuildPackageFromItems([first, first]);

        var exception = Assert.Throws<IngestionApplicationException>(() =>
            IngestionPackageValidator.ValidatePackage(fixture.Package, fixture.ManifestDigest));

        Assert.Equal("INGESTION_ITEM_KEY_DUPLICATE", exception.Code);
        Assert.Equal("Ingestion.Integrity", exception.Owner);
    }

    [Fact]
    public void ModifiedItemAfterSealingRejectsWholePackage()
    {
        var fixture = BuildPackage();
        var original = Assert.Single(fixture.Package.Items);
        var modified = original with
        {
            CategoryProposals = ["recording-studio", "podcast-studio"],
        };
        var package = fixture.Package with { Items = [modified] };

        var exception = Assert.Throws<IngestionApplicationException>(() =>
            IngestionPackageValidator.ValidatePackage(package, fixture.ManifestDigest));

        Assert.Equal("INGESTION_ITEM_DIGEST_MISMATCH", exception.Code);
    }

    [Fact]
    public void InternalReviewOnlyEvidenceProducesExplicitReviewDecision()
    {
        var fixture = BuildPackage(CandidateUsagePolicyContract.InternalReviewOnly);

        var result = IngestionPackageValidator.ValidatePackage(
            fixture.Package,
            fixture.ManifestDigest);

        var item = Assert.Single(result.Items);
        Assert.Equal(ImportItemDecisionKind.NeedsReview, item.Decision);
        Assert.Contains("INGESTION_PROVENANCE_REVIEW_REQUIRED", item.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockingQualityProducesRejectedItemWithoutMaskingPackageIntegrity()
    {
        var fixture = BuildPackage(
            CandidateUsagePolicyContract.Publishable,
            IngestionQualityStateContract.Blocked);

        var result = IngestionPackageValidator.ValidatePackage(
            fixture.Package,
            fixture.ManifestDigest);

        var item = Assert.Single(result.Items);
        Assert.Equal(ImportItemDecisionKind.Rejected, item.Decision);
        Assert.Contains("INGESTION_QUALITY_BLOCKED", item.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownWireRevisionFailsClosed()
    {
        var fixture = BuildPackage();
        var manifest = fixture.Package.Manifest with { ContractRevision = 99 };
        var digest = IngestionPackageValidator.ComputeManifestDigest(manifest);

        var exception = Assert.Throws<IngestionApplicationException>(() =>
            IngestionPackageValidator.ValidateManifest(manifest, digest));

        Assert.Equal("INGESTION_CONTRACT_UNSUPPORTED", exception.Code);
    }

    private static PackageFixture BuildPackage(
        CandidateUsagePolicyContract usagePolicy = CandidateUsagePolicyContract.Publishable,
        IngestionQualityStateContract qualityState = IngestionQualityStateContract.Passed)
    {
        return BuildPackageFromItems([CreateItem("candidate-1", usagePolicy, qualityState)], usagePolicy);
    }

    private static PackageFixture BuildPackageFromItems(
        IReadOnlyList<IngestionItemContract> items,
        CandidateUsagePolicyContract usagePolicy = CandidateUsagePolicyContract.Publishable)
    {
        var itemIndexDigest = IngestionPackageValidator.ComputeItemIndexDigest(items);
        var payloadDigest = IngestionPackageValidator.ComputePayloadDigest(items);
        var manifest = new AggregatorCandidateIngestionManifest(
            AggregatorCandidateIngestionContract.Identity,
            AggregatorCandidateIngestionContract.Revision,
            "collector-berlin",
            "build-2026-08-04",
            Guid.Parse("0198a123-0000-7000-8000-000000000001"),
            new string('a', 64),
            "berlin-recording",
            "berlin-recording-services",
            Guid.Parse("0198a123-0000-7000-8000-000000000002"),
            CreatedAt,
            items.Count,
            itemIndexDigest,
            payloadDigest,
            [
                new IngestionSourcePolicyReferenceContract(
                    "official-website",
                    new string('b', 64),
                    usagePolicy),
            ],
            [
                new IngestionPackageArtifactContract(
                    IngestionArtifactRoleContract.CandidatePayload,
                    "ingestion/quarantine/package.json",
                    new string('c', 64),
                    4_096,
                    "application/json"),
            ]);
        var manifestDigest = IngestionPackageValidator.ComputeManifestDigest(manifest);
        return new PackageFixture(
            new AggregatorCandidateIngestionPackage(manifest, items),
            manifestDigest);
    }

    private static IngestionItemContract CreateItem(
        string itemKey,
        CandidateUsagePolicyContract usagePolicy = CandidateUsagePolicyContract.Publishable,
        IngestionQualityStateContract qualityState = IngestionQualityStateContract.Passed)
    {
        var qualityIssues = qualityState == IngestionQualityStateContract.Blocked
            ? new[]
            {
                new IngestionQualityIssueContract(
                    "MISSING_REQUIRED_PROVENANCE",
                    IngestionQualitySeverityContract.Blocking,
                    "Provide accepted field-level provenance."),
            }
            : Array.Empty<IngestionQualityIssueContract>();
        var item = new IngestionItemContract(
            itemKey,
            Guid.Parse("0198a123-0000-7000-8000-000000000010"),
            3,
            IngestionEntityKindContract.Place,
            new IngestionSubjectProposalContract(
                "website:example-studio",
                "example-studio.invalid",
                new string('d', 64),
                "de|10115|berlin|invalidenstrasse|1",
                ["website:example-studio.invalid"]),
            [
                new LocalizedCandidateTextContract(
                    "de-DE",
                    CandidateFieldStateContract.Observed,
                    "Example Studio",
                    "localizedNames/de-DE"),
            ],
            ["recording-studio"],
            [
                new CandidateAttributeProposalContract(
                    "vocal-booth",
                    CandidateFieldStateContract.Observed,
                    new CandidateTypedValueContract(
                        CandidateValueKindContract.Boolean,
                        true,
                        null,
                        null,
                        null),
                    "attributes/vocal-booth"),
            ],
            [
                new CandidateContactProposalContract(
                    CandidateContactKindContract.Website,
                    CandidateFieldStateContract.Observed,
                    "https://example-studio.invalid/",
                    "Website",
                    "contacts/website"),
            ],
            [
                new CandidateExternalReferenceProposalContract(
                    "official-website",
                    "example-studio.invalid",
                    "https://example-studio.invalid/",
                    "canonical-profile",
                    usagePolicy,
                    "externalReferences/official-website"),
            ],
            new CandidateGeographyProposalContract(
                CandidateGeographyStateContract.ProposedPoint,
                52.5200m,
                13.4050m,
                "DE",
                "mitte",
                "geography/point"),
            Array.Empty<CandidateRelationshipProposalContract>(),
            [
                new CandidateProvenanceReferenceContract(
                    Guid.Parse("0198a123-0000-7000-8000-000000000011"),
                    "localizedNames/de-DE",
                    "official-website",
                    "example-studio.invalid",
                    "https://example-studio.invalid/impressum",
                    CreatedAt,
                    usagePolicy,
                    new string('e', 64),
                    usagePolicy == CandidateUsagePolicyContract.DisplayWithAttribution
                        ? "Example Studio"
                        : null),
            ],
            new IngestionQualitySummaryContract(qualityState, qualityIssues),
            Array.Empty<CollectorReviewReferenceContract>(),
            new string('0', 64));
        return item with
        {
            ContentDigest = IngestionPackageValidator.ComputeItemContentDigest(item),
        };
    }

    private sealed record PackageFixture(
        AggregatorCandidateIngestionPackage Package,
        string ManifestDigest);
}
