using System.Security.Cryptography;
using System.Text;
using Aggregator.Acceptance.Contracts;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var internalKey = builder.Configuration["Acceptance:InternalKey"];
if (string.IsNullOrWhiteSpace(internalKey) || internalKey.Length < 32)
{
    throw new InvalidOperationException(
        "Acceptance:InternalKey must contain at least 32 characters.");
}

builder.Services.AddCatalogApplication();
builder.Services.AddCatalogInfrastructure(builder.Configuration);

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new
{
    owner = "Acceptance.Control",
    state = "live",
}));
app.MapGet("/health/ready", async (
    CatalogReadinessProbe readinessProbe,
    CancellationToken cancellationToken) =>
{
    var readiness = await readinessProbe.CheckAsync(cancellationToken);
    return readiness.Ready
        ? Results.Ok(new { owner = "Acceptance.Control", state = readiness.State })
        : Results.Json(
            new
            {
                owner = "Acceptance.Control",
                state = readiness.State,
                failureType = readiness.FailureType,
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapPost("/acceptance/catalog/seed", async (
    HttpRequest httpRequest,
    CatalogSeedRequest request,
    CatalogConfigurationService configurationService,
    CatalogListingService listingService,
    CatalogPublicationService publicationService,
    CancellationToken cancellationToken) =>
{
    if (!HasValidInternalKey(httpRequest, internalKey))
    {
        return Results.Unauthorized();
    }

    ValidateCandidate(request.Title, request.SourceReference, request.EvidenceDigest, request.Website);
    var actor = CatalogActor.Create(AcceptanceIds.ActorId);
    _ = await configurationService.ImportAsync(
        new ImportProductConfigurationRequest(
            CatalogContractIdentity.ProductConfiguration,
            CatalogContractIdentity.ProductConfigurationRevision,
            AcceptanceIds.ConfigurationDigest,
            CreateConfiguration()),
        actor,
        cancellationToken);
    _ = await configurationService.ActivateAsync(
        AcceptanceIds.CatalogKey,
        new ActivateProductConfigurationRequest(
            AcceptanceIds.ConfigurationRevisionId,
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
        new CreateListingRequest(AcceptanceIds.CatalogKey, subject),
        actor,
        cancellationToken);
    var revision = await listingService.AddRevisionAsync(
        listing.Id,
        new CreateListingRevisionRequest(
            ExpectedVersion: 1,
            AcceptanceIds.ConfigurationRevisionId,
            subject,
            CreateContent(
                request.Title,
                request.SourceReference,
                request.EvidenceDigest,
                request.Website,
                request.HourlyPrice)),
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
            AcceptanceIds.CatalogKey,
            AcceptanceIds.ConfigurationRevisionId,
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

    return Results.Ok(new CatalogSeedResponse(
        AcceptanceIds.ConfigurationRevisionId,
        listing.Id,
        revision.Id,
        publication.Id,
        new SubjectReferenceResponse(
            request.SubjectId,
            request.SubjectRevisionId,
            "place"),
        ExpectedListingVersionAfterPublication: 4));
});
app.MapPost("/acceptance/catalog/publish-next", async (
    HttpRequest httpRequest,
    CatalogPublishNextRequest request,
    CatalogListingService listingService,
    CatalogPublicationService publicationService,
    CancellationToken cancellationToken) =>
{
    if (!HasValidInternalKey(httpRequest, internalKey))
    {
        return Results.Unauthorized();
    }

    ValidateCandidate(request.Title, request.SourceReference, request.EvidenceDigest, request.Website);
    if (request.ListingId == Guid.Empty || request.FirstPublicationId == Guid.Empty)
    {
        return Results.BadRequest(new
        {
            code = "ACCEPTANCE_CATALOG_IDENTITY_INVALID",
        });
    }

    var actor = CatalogActor.Create(AcceptanceIds.ActorId);
    var subject = new SubjectReferenceContract(
        request.SubjectId,
        request.SubjectRevisionId,
        SubjectKindContract.Place);
    var revision = await listingService.AddRevisionAsync(
        request.ListingId,
        new CreateListingRevisionRequest(
            ExpectedVersion: 4,
            AcceptanceIds.ConfigurationRevisionId,
            subject,
            CreateContent(
                request.Title,
                request.SourceReference,
                request.EvidenceDigest,
                request.Website,
                request.HourlyPrice)),
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
            AcceptanceIds.CatalogKey,
            AcceptanceIds.ConfigurationRevisionId,
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
    return Results.Ok(new CatalogPublishNextResponse(
        revision.Id,
        publication.Id,
        ExpectedListingVersionAfterPublication: 7));
});
app.MapPost("/acceptance/catalog/rollback", async (
    HttpRequest httpRequest,
    CatalogRollbackRequest request,
    CatalogPublicationService publicationService,
    CancellationToken cancellationToken) =>
{
    if (!HasValidInternalKey(httpRequest, internalKey))
    {
        return Results.Unauthorized();
    }

    if (request.TargetPublicationId == Guid.Empty ||
        request.ExpectedCurrentPublicationId == Guid.Empty)
    {
        return Results.BadRequest(new
        {
            code = "ACCEPTANCE_ROLLBACK_IDENTITY_INVALID",
        });
    }

    var rollback = await publicationService.RollbackAsync(
        AcceptanceIds.CatalogKey,
        new RollbackPublicationRequest(
            request.TargetPublicationId,
            request.ExpectedCurrentPublicationId),
        CatalogActor.Create(AcceptanceIds.ActorId),
        cancellationToken);
    return Results.Ok(new CatalogRollbackResponse(
        rollback.Id,
        rollback.Sequence,
        rollback.IsCurrent));
});

await app.RunAsync();

static bool HasValidInternalKey(HttpRequest request, string expectedKey)
{
    if (!request.Headers.TryGetValue("X-Acceptance-Key", out var suppliedValues))
    {
        return false;
    }

    var suppliedKey = suppliedValues.ToString();
    if (string.IsNullOrWhiteSpace(suppliedKey))
    {
        return false;
    }

    var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
    var suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
    return CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
}

static void ValidateCandidate(
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
        throw new ArgumentException("Website must be an absolute HTTP URL.", nameof(website));
    }
}

static ProductConfigurationContract CreateConfiguration() =>
    new(
        AcceptanceIds.ConfigurationRevisionId,
        AcceptanceIds.ConfigurationTimestamp,
        new SiteDefinitionContract(
            "berlin-recording",
            "de-DE",
            ["en-GB", "de-DE"],
            "EUR",
            "Europe/Berlin"),
        new CatalogDefinitionContract(
            AcceptanceIds.CatalogKey,
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

static ListingRevisionContentContract CreateContent(
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

static ProvenanceAssertionContract Assertion(
    Guid id,
    string sourceReference,
    string evidenceDigest,
    string field) =>
    new(
        id,
        SourceKindContract.PublicWebsite,
        $"{sourceReference.Trim()}#{field}",
        AcceptanceIds.CandidateObservedAtUtc,
        DateTimeOffset.UtcNow,
        UsagePolicyContract.PublicAllowed,
        evidenceDigest);

internal static class AcceptanceIds
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

public partial class Program;
