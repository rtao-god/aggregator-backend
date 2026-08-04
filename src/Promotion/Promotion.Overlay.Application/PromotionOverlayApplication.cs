using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aggregator.Promotion.Contracts;

namespace Aggregator.Promotion.Overlay.Application;

public sealed record PromotionOverlayPublication(
    Guid CommandId,
    Guid OverlayId,
    string CatalogKey,
    Guid SourcePublicReadRevisionId,
    long ActivationRevision,
    string ContentDigest,
    IReadOnlyList<PromotionOverlayItemContract> Items,
    DateTimeOffset CreatedAtUtc);

public sealed record PromotionOverlayOutboxMessage(
    Guid EventId,
    string RoutingKey,
    string ContractIdentity,
    string PayloadJson,
    string PayloadDigest,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId);

public sealed record PromotionOverlayCommitResult(
    PromotionOverlayPublication Publication,
    bool Replayed);

public interface IPromotionOverlayStore
{
    public Task<long> GetNextActivationRevisionAsync(
        string catalogKey,
        CancellationToken cancellationToken);

    public Task<PromotionOverlayCommitResult> CommitAsync(
        PromotionOverlayPublication publication,
        Guid? expectedCurrentOverlayId,
        string commandDigest,
        PromotionOverlayOutboxMessage outboxMessage,
        CancellationToken cancellationToken);
}

public interface IPromotionOverlayIdSource
{
    public Guid CreateId();
}

public sealed class PromotionOverlayException : InvalidOperationException
{
    public PromotionOverlayException(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        Code = code;
        StatusCode = statusCode;
        RequiredAction = requiredAction;
        Context = context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Code { get; }

    public int StatusCode { get; }

    public string RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }
}

public sealed class PromotionOverlayPublicationService(
    IPromotionOverlayStore store,
    IPromotionOverlayIdSource idSource,
    TimeProvider timeProvider)
{
    private static readonly Regex CatalogKeyPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task<PromotionOverlayPublicationResponse> PublishAsync(
        PublishPromotionOverlayRequest request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var normalized = Normalize(request);
        var commandDigest = ComputeDigest(normalized);
        var activationRevision = await store.GetNextActivationRevisionAsync(
            normalized.CatalogKey,
            cancellationToken);
        var createdAtUtc = timeProvider.GetUtcNow();
        var overlayId = idSource.CreateId();
        var contentDigest = ComputeDigest(new
        {
            overlayId,
            normalized.CatalogKey,
            normalized.SourcePublicReadRevisionId,
            activationRevision,
            items = normalized.Items,
        });
        var publication = new PromotionOverlayPublication(
            normalized.CommandId,
            overlayId,
            normalized.CatalogKey,
            normalized.SourcePublicReadRevisionId,
            activationRevision,
            contentDigest,
            normalized.Items,
            createdAtUtc);
        var integrationEvent = new PromotionOverlayActivated(
            idSource.CreateId(),
            overlayId,
            normalized.CatalogKey,
            normalized.SourcePublicReadRevisionId,
            activationRevision,
            contentDigest,
            normalized.Items,
            createdAtUtc);
        var payloadJson = Serialize(integrationEvent);
        var outboxMessage = new PromotionOverlayOutboxMessage(
            integrationEvent.EventId,
            PromotionOverlayContractIdentity.RoutingKey,
            PromotionOverlayContractIdentity.ActivationEvent,
            payloadJson,
            ComputeSha256(Encoding.UTF8.GetBytes(payloadJson)),
            createdAtUtc,
            correlationId.Trim(),
            normalized.CommandId);
        var result = await store.CommitAsync(
            publication,
            normalized.ExpectedCurrentOverlayId,
            commandDigest,
            outboxMessage,
            cancellationToken);
        return new PromotionOverlayPublicationResponse(
            result.Publication.CommandId,
            result.Publication.OverlayId,
            result.Publication.CatalogKey,
            result.Publication.SourcePublicReadRevisionId,
            result.Publication.ActivationRevision,
            result.Publication.ContentDigest,
            result.Publication.CreatedAtUtc,
            IsCurrent: true,
            result.Replayed);
    }

    private static PublishPromotionOverlayRequest Normalize(PublishPromotionOverlayRequest request)
    {
        if (request.CommandId == Guid.Empty)
        {
            throw Failure(
                "PROMOTION_COMMAND_ID_INVALID",
                400,
                "Promotion overlay command ID is required.",
                "Generate one UUIDv7 command ID and replay only the exact same command under it.");
        }

        if (string.IsNullOrWhiteSpace(request.CatalogKey))
        {
            throw Failure(
                "PROMOTION_CATALOG_KEY_REQUIRED",
                400,
                "Catalog key is required.",
                "Submit the exact public catalog key.");
        }

        var catalogKey = request.CatalogKey.Trim();
        if (catalogKey.Length > 96 || !CatalogKeyPattern.IsMatch(catalogKey))
        {
            throw Failure(
                "PROMOTION_CATALOG_KEY_INVALID",
                400,
                "Catalog key is not a normalized lower-case identifier.",
                "Submit a lower-case hyphen-separated catalog key.");
        }

        if (request.SourcePublicReadRevisionId == Guid.Empty)
        {
            throw Failure(
                "PROMOTION_SOURCE_REVISION_INVALID",
                400,
                "Source public read revision ID is required.",
                "Build the overlay against one exact Query public read revision.");
        }

        if (request.ExpectedCurrentOverlayId == Guid.Empty)
        {
            throw Failure(
                "PROMOTION_POINTER_EXPECTATION_INVALID",
                400,
                "Expected current overlay ID cannot be an empty GUID.",
                "Omit it for explicit absence or submit the exact current overlay ID.");
        }

        var sourceItems = request.Items
            ?? throw Failure(
                "PROMOTION_ITEMS_REQUIRED",
                400,
                "Promotion overlay items are required.",
                "Submit between one and 100 exact sponsored placements.");
        if (sourceItems.Count is < 1 or > 100)
        {
            throw Failure(
                "PROMOTION_ITEM_COUNT_INVALID",
                400,
                "Promotion overlay must contain between one and 100 items.",
                "Bound the overlay to the declared public placement capacity.");
        }

        var items = sourceItems.Select(NormalizeItem).OrderBy(item => item.Position).ToArray();
        var duplicatePositions = items.GroupBy(item => item.Position).Where(group => group.Count() > 1).ToArray();
        if (duplicatePositions.Length > 0)
        {
            throw Failure(
                "PROMOTION_POSITION_DUPLICATE",
                400,
                "Promotion overlay contains duplicate placement positions.",
                "Assign one unique position to every sponsored listing.");
        }

        var duplicateListings = items.GroupBy(item => item.ListingId).Where(group => group.Count() > 1).ToArray();
        if (duplicateListings.Length > 0)
        {
            throw Failure(
                "PROMOTION_LISTING_DUPLICATE",
                400,
                "Promotion overlay contains the same listing more than once.",
                "Publish at most one sponsored placement per listing.");
        }

        return new PublishPromotionOverlayRequest(
            request.CommandId,
            catalogKey,
            request.SourcePublicReadRevisionId,
            request.ExpectedCurrentOverlayId,
            Array.AsReadOnly(items));
    }

    private static PromotionOverlayItemContract NormalizeItem(PromotionOverlayItemContract item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ListingId == Guid.Empty || item.CampaignId == Guid.Empty)
        {
            throw Failure(
                "PROMOTION_ITEM_IDENTITY_INVALID",
                400,
                "Every promotion item requires non-empty listing and campaign IDs.",
                "Submit the exact listing and campaign identities.");
        }

        if (item.Position is < 1 or > 100)
        {
            throw Failure(
                "PROMOTION_POSITION_INVALID",
                400,
                "Promotion position must be between one and 100.",
                "Assign a bounded one-based placement position.");
        }

        var locale = RequireText(item.Locale, 35, "locale");
        try
        {
            locale = CultureInfo.GetCultureInfo(locale).Name;
        }
        catch (CultureNotFoundException exception)
        {
            throw Failure(
                "PROMOTION_LOCALE_INVALID",
                400,
                $"Promotion locale '{locale}' is invalid.",
                "Submit a valid locale code supported by the source public revision.",
                innerException: exception);
        }

        var routePath = RequireText(item.RoutePath, 500, "routePath");
        if (!routePath.StartsWith('/') || routePath.Contains("..", StringComparison.Ordinal))
        {
            throw Failure(
                "PROMOTION_ROUTE_INVALID",
                400,
                "Promotion route must be an absolute traversal-free site path.",
                "Copy the exact public route from Query.");
        }

        return new PromotionOverlayItemContract(
            item.ListingId,
            item.CampaignId,
            item.Position,
            locale,
            RequireText(item.Title, 300, "title"),
            routePath,
            RequireText(item.DisclosureLabel, 100, "disclosureLabel"));
    }

    private static string RequireText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Failure(
                "PROMOTION_TEXT_REQUIRED",
                400,
                $"Promotion field '{field}' is required.",
                "Submit the exact bounded public display value.");
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length > maximumLength)
        {
            throw Failure(
                "PROMOTION_TEXT_TOO_LONG",
                400,
                $"Promotion field '{field}' exceeds {maximumLength} characters.",
                "Shorten the public display value before publication.");
        }

        return normalized;
    }

    private static string ComputeDigest<T>(T value) =>
        ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));

    private static string ComputeSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static PromotionOverlayException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null) =>
        new(code, statusCode, message, requiredAction, context, innerException);
}

public sealed class UuidV7PromotionOverlayIdSource : IPromotionOverlayIdSource
{
    public Guid CreateId() => Guid.CreateVersion7();
}
