using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Acceptance.Control;

public sealed class AcceptanceCatalogScenarioService(
    CatalogConfigurationService configurationService,
    CatalogListingService listingService,
    CatalogPublicationService publicationService)
{
    public async Task<CatalogSeedResponse> SeedAsync(
        CatalogSeedRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCandidate(request.Title, request.SourceReference, request.EvidenceDigest, request.Website);
        if (request.SubjectId == Guid.Empty || request.SubjectRevisionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Acceptance candidate requires non-empty subject identities.",
                nameof(request));
        }

        var actor = CatalogActor.Create(AcceptanceCatalogConstants.ActorId);
        _ = await configurationService.ImportAsync(
            new ImportProductConfigurationRequest(
                CatalogContractIdentity.ProductConfiguration,
                CatalogContractIdentity.ProductConfigurationRevision,
                AcceptanceCatalogConstants.ConfigurationDigest,
                CreateConfiguration()),
            cancellationToken);
        _ = await configurationService.ActivateAsync(
            AcceptanceCatalogConstants.CatalogKey,
            new ActivateProductConfigurationRequest(
                AcceptanceCatalogConstants.ConfigurationRevisionId,
                new ConfigurationPointerExpectationContract(
                    PointerExpectationKindContract.Absent,
                    ConfigurationRevisionId: null)),
            actor,
            cancellationToken);

        var subject = new SubjectReferenceContract(
            request.SubjectId,
            request.SubjectRevisionId,
            SubjectKindContract.Place);
        var listing = await listingService.CreateAsync(
            new CreateListingRequest(AcceptanceCatalogConstants.CatalogKey, subject),
            actor,
            cancellationToken);
        var revision = await listingService.AddRevisionAsync(
            listing.Id,
            new CreateListingRevisionRequest(
                ExpectedVersion: 1,
                AcceptanceCatalogConstants.ConfigurationRevisionId,
                subject,
                CreateContent(request)),
            actor,
            cancellationToken);
        _ = await listingService.ApproveAsync(
            listing.Id,
            new ApproveListingRevisionRequest(
                ExpectedVersion: 2,
                RevisionId: revision.Id),
            actor,
            cancellationToken);
        var publication = await publicationService.PublishAsync(
            new CreateCatalogPublicationRequest(
                AcceptanceCatalogConstants.CatalogKey,
                AcceptanceCatalogConstants.ConfigurationRevisionId,
                new PublicationPointerExpectationContract(
                    PointerExpectationKindContract.Absent,
                    PublicationId: null),
                [
                    new PublicationSelectionContract(
                        listing.Id,
                        revision.Id,
                        ExpectedListingVersion: 3),
                ]),
            actor,
            cancellationToken);
        return new CatalogSeedResponse(
            AcceptanceCatalogConstants.ConfigurationRevisionId,
            listing.Id,
            revision.Id,
            publication.Id,
            ExpectedListingVersionAfterPublication: 4);
    }

    public async Task<CatalogPublishNextResponse> PublishNextAsync(
        CatalogPublishNextRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCandidate(request.Title, request.SourceReference, request.EvidenceDigest, request.Website);
        if (request.ListingId == Guid.Empty ||
            request.FirstPublicationId == Guid.Empty ||
            request.SubjectId == Guid.Empty ||
            request.SubjectRevisionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Acceptance next publication requires non-empty identities.",
                nameof(request));
        }

        var actor = CatalogActor.Create(AcceptanceCatalogConstants.ActorId);
        var subject = new SubjectReferenceContract(
            request.SubjectId,
            request.SubjectRevisionId,
            SubjectKindContract.Place);
        var revision = await listingService.AddRevisionAsync(
            request.ListingId,
            new CreateListingRevisionRequest(
                ExpectedVersion: 4,
                AcceptanceCatalogConstants.ConfigurationRevisionId,
                subject,
                CreateContent(request)),
            actor,
            cancellationToken);
        _ = await listingService.ApproveAsync(
            request.ListingId,
            new ApproveListingRevisionRequest(
                ExpectedVersion: 5,
                RevisionId: revision.Id),
            actor,
            cancellationToken);
        var publication = await publicationService.PublishAsync(
            new CreateCatalogPublicationRequest(
                AcceptanceCatalogConstants.CatalogKey,
                AcceptanceCatalogConstants.ConfigurationRevisionId,
                new PublicationPointerExpectationContract(
                    PointerExpectationKindContract.Exact,
                    request.FirstPublicationId),
                [
                    new PublicationSelectionContract(
                        request.ListingId,
                        revision.Id,
                        ExpectedListingVersion: 6),
                ]),
            actor,
            cancellationToken);
        return new CatalogPublishNextResponse(
            revision.Id,
            publication.Id,
            ExpectedListingVersionAfterPublication: 7);
    }

    public async Task<CatalogRollbackResponse> RollbackAsync(
        CatalogRollbackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetPublicationId == Guid.Empty ||
            request.ExpectedCurrentPublicationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Acceptance rollback requires non-empty publication identities.",
                nameof(request));
        }

        var rollback = await publicationService.RollbackAsync(
            AcceptanceCatalogConstants.CatalogKey,
            new RollbackPublicationRequest(
                request.TargetPublicationId,
                request.ExpectedCurrentPublicationId),
            CatalogActor.Create(AcceptanceCatalogConstants.ActorId),
            cancellationToken);
        return new CatalogRollbackResponse(
            rollback.Id,
            rollback.Sequence,
            rollback.IsCurrent);
    }

    private static ProductConfigurationContract CreateConfiguration() =>
        new(
            AcceptanceCatalogConstants.ConfigurationRevisionId,
            AcceptanceCatalogConstants.ConfigurationTimestamp,
            new SiteDefinitionContract(
                "berlin-recording",
                "de-DE",
                ["en-GB", "de-DE"],
                "EUR",
                "Europe/Berlin"),
            new CatalogDefinitionContract(
                AcceptanceCatalogConstants.CatalogKey,
                "berlin-recording",
                "berlin-core-and-nearby",
                "EUR",
                "Europe/Berlin",
                [SubjectKindContract.Provider, SubjectKindContract.Place]),
            [
                new CategoryDefinitionContract(
                    "recording-studio",
                    [SubjectKindContract.Place],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["de-DE"] = "Tonstudio",
                        ["en-GB"] = "Recording studio",
                    },
                    IsActive: true),
                new CategoryDefinitionContract(
                    "music-producer",
                    [SubjectKindContract.Provider],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["de-DE"] = "Musikproduzent",
                        ["en-GB"] = "Music producer",
                    },
                    IsActive: true),
            ],
            [
                new AttributeDefinitionContract(
                    "hourly-price",
                    AttributeValueKindContract.Decimal,
                    AttributeCardinalityContract.Single,
                    PublicFieldRequirementContract.Optional,
                    ["recording-studio"],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["de-DE"] = "Stundenpreis",
                        ["en-GB"] = "Hourly price",
                    },
                    Minimum: null,
                    Maximum: null,
                    AllowedValues: [],
                    IsFilterable: true,
                    IsSortable: true),
            ]);

    private static ListingRevisionContentContract CreateContent(CatalogSeedRequest request) =>
        CreateContent(
            request.Title,
            request.SourceReference,
            request.EvidenceDigest,
            request.Website,
            request.HourlyPrice);

    private static ListingRevisionContentContract CreateContent(
        CatalogPublishNextRequest request) =>
        CreateContent(
            request.Title,
            request.SourceReference,
            request.EvidenceDigest,
            request.Website,
            request.HourlyPrice);

    private static ListingRevisionContentContract CreateContent(
        string title,
        string sourceReference,
        string evidenceDigest,
        string website,
        decimal hourlyPrice)
    {
        var nameAssertionId = Guid.CreateVersion7();
        var categoryAssertionId = Guid.CreateVersion7();
        var geographyAssertionId = Guid.CreateVersion7();
        var contactAssertionId = Guid.CreateVersion7();
        var priceAssertionId = Guid.CreateVersion7();
        return new ListingRevisionContentContract(
            [
                new LocalizedTextValueContract(
                    "de-DE",
                    FieldValueStateContract.Observed,
                    title.Trim(),
                    nameAssertionId,
                    MissingReason: null),
                new LocalizedTextValueContract(
                    "en-GB",
                    FieldValueStateContract.Missing,
                    Value: null,
                    AssertionId: null,
                    MissingValueReasonContract.NotPublishedBySource),
            ],
            [
                new LocalizedTextValueContract(
                    "de-DE",
                    FieldValueStateContract.Missing,
                    Value: null,
                    AssertionId: null,
                    MissingValueReasonContract.NotPublishedBySource),
            ],
            [new CategoryAssignmentContract("recording-studio", categoryAssertionId)],
            [
                new ListingAttributeValueContract(
                    "hourly-price",
                    FieldValueStateContract.Observed,
                    new TypedValueContract(
                        AttributeValueKindContract.Decimal,
                        BooleanValue: null,
                        DecimalValue: hourlyPrice,
                        TextValue: null,
                        TextSetValue: null),
                    priceAssertionId,
                    MissingReason: null),
            ],
            new GeographyValueContract(
                GeographyStateContract.PrimaryMarket,
                Latitude: 52.520008m,
                Longitude: 13.404954m,
                DistrictKey: "mitte",
                geographyAssertionId),
            [
                new ContactValueContract(
                    ContactKindContract.Website,
                    website.Trim(),
                    Label: null,
                    contactAssertionId),
            ],
            Media: [],
            Assertions:
            [
                Assertion(nameAssertionId, sourceReference, evidenceDigest, "name"),
                Assertion(categoryAssertionId, sourceReference, evidenceDigest, "category"),
                Assertion(geographyAssertionId, sourceReference, evidenceDigest, "geography"),
                Assertion(contactAssertionId, sourceReference, evidenceDigest, "contact"),
                Assertion(priceAssertionId, sourceReference, evidenceDigest, "price"),
            ]);
    }

    private static ProvenanceAssertionContract Assertion(
        Guid id,
        string sourceReference,
        string evidenceDigest,
        string field) =>
        new(
            id,
            SourceKindContract.PublicWebsite,
            $"{sourceReference.Trim()}#{field}",
            AcceptanceCatalogConstants.CandidateObservedAtUtc,
            DateTimeOffset.UtcNow,
            UsagePolicyContract.PublicAllowed,
            evidenceDigest);

    private static void ValidateCandidate(
        string title,
        string sourceReference,
        string evidenceDigest,
        string website)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(website);
        if (evidenceDigest.Length != 64 ||
            evidenceDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Evidence digest must be a lowercase SHA-256 hexadecimal value.",
                nameof(evidenceDigest));
        }

        if (!Uri.TryCreate(website, UriKind.Absolute, out var websiteUri) ||
            websiteUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "Website must be an absolute HTTP URL.",
                nameof(website));
        }
    }
}

public static class AcceptanceCatalogConstants
{
    public const string CatalogKey = "berlin-recording-services";

    public const string ConfigurationDigest =
        "ac8b665d754ac21f0ec66fab729e1a669de53effaed4120a6e004dc9f7785f31";

    public static readonly Guid ConfigurationRevisionId =
        Guid.Parse("0192f5f0-0000-7000-8000-000000000001");

    public static readonly Guid ActorId =
        Guid.Parse("0192f5f0-0000-7000-8000-000000000004");

    public static readonly DateTimeOffset ConfigurationTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset CandidateObservedAtUtc =
        new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
}

public sealed record CatalogSeedRequest(
    Guid SubjectId,
    Guid SubjectRevisionId,
    string Title,
    string SourceReference,
    string EvidenceDigest,
    string Website,
    decimal HourlyPrice);

public sealed record CatalogSeedResponse(
    Guid ConfigurationRevisionId,
    Guid ListingId,
    Guid ListingRevisionId,
    Guid PublicationId,
    long ExpectedListingVersionAfterPublication);

public sealed record CatalogPublishNextRequest(
    Guid ListingId,
    Guid FirstPublicationId,
    Guid SubjectId,
    Guid SubjectRevisionId,
    string Title,
    string SourceReference,
    string EvidenceDigest,
    string Website,
    decimal HourlyPrice);

public sealed record CatalogPublishNextResponse(
    Guid ListingRevisionId,
    Guid PublicationId,
    long ExpectedListingVersionAfterPublication);

public sealed record CatalogRollbackRequest(
    Guid TargetPublicationId,
    Guid ExpectedCurrentPublicationId);

public sealed record CatalogRollbackResponse(
    Guid CurrentPublicationId,
    long PublicationSequence,
    bool IsCurrent);
