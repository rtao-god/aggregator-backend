
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.CatalogMedia.Contracts;
using Aggregator.CatalogMedia.Domain;

namespace Aggregator.CatalogMedia.Application;

public interface ICatalogMediaClock
{
    public DateTimeOffset GetUtcNow();
}

public interface ICatalogMediaIdSource
{
    public Guid CreateId();
}

public sealed record CatalogMediaActor(Guid Id)
{
    public static CatalogMediaActor Create(Guid id)
    {
        if (id == Guid.Empty) throw new CatalogMediaApplicationException(
            "CatalogMedia.Access", "CATALOG_MEDIA_ACTOR_REQUIRED", 403,
            "Catalog media actor identity is required.",
            "Authenticate through an identity mapped to one internal actor.");
        return new CatalogMediaActor(id);
    }
}

public sealed record CatalogMediaCommandContext(
    CatalogMediaActor Actor,
    string CorrelationId,
    Guid? CausationId)
{
    public static CatalogMediaCommandContext Start(CatalogMediaActor actor, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var value = string.IsNullOrWhiteSpace(correlationId) ? Guid.CreateVersion7().ToString("D") : correlationId.Trim();
        if (value.Length > 128 || value.Any(char.IsControl))
            throw new CatalogMediaApplicationException(
                "CatalogMedia.Commands", "CATALOG_MEDIA_CORRELATION_INVALID", 400,
                "Catalog media correlation identity is invalid.",
                "Use a printable correlation identity of at most 128 characters.");
        return new CatalogMediaCommandContext(actor, value, null);
    }
}

public sealed record CatalogMediaCommandIdentity(string Scope, string Key, string RequestDigest)
{
    public static CatalogMediaCommandIdentity Create(string scope, string key, string requestDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (scope.Length > 180 || key.Length > 200 || key.Any(char.IsControl))
            throw new CatalogMediaApplicationException(
                "CatalogMedia.Commands", "CATALOG_MEDIA_IDEMPOTENCY_INVALID", 400,
                "Catalog media idempotency identity is invalid.",
                "Submit one stable printable Idempotency-Key.");
        CatalogMediaCanonicalJson.RequireDigest(requestDigest, nameof(requestDigest));
        return new CatalogMediaCommandIdentity(scope.Trim(), key.Trim(), requestDigest);
    }
}

public sealed record CatalogMediaOutboxMessage(
    Guid Id,
    string RoutingKey,
    string ContractIdentity,
    string PayloadJson,
    string PayloadDigest,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId);

public sealed record CatalogMediaCommandResult(CatalogMediaAsset Asset, bool Replayed);

public sealed record CatalogMediaUploadAuthorization(
    Uri UploadUri,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyDictionary<string, string> RequiredHeaders);

public sealed record CatalogMediaObjectDescriptor(
    string ObjectKey,
    string ContentType,
    string ContentDigest,
    long Size);

public sealed record CatalogMediaVariantContent(
    CatalogMediaVariantKind Kind,
    string ContentType,
    ReadOnlyMemory<byte> Content,
    int Width,
    int Height);

public sealed record CatalogMediaScanResult(bool IsClean, string? ThreatName);

public sealed record CatalogMediaProcessingLease(
    Guid AssetId,
    Guid LeaseToken,
    int AttemptCount,
    DateTimeOffset LeaseExpiresAtUtc,
    long StoredAggregateRevision,
    CatalogMediaAsset Asset);

public interface ICatalogMediaRepository
{
    public Task<CatalogMediaCommandResult?> ReadCommandResultAsync(
        CatalogMediaCommandIdentity commandIdentity,
        CancellationToken cancellationToken);
    public Task<CatalogMediaCommandResult> AddAsync(
        CatalogMediaAsset asset,
        CatalogMediaCommandIdentity commandIdentity,
        CatalogMediaCommandContext context,
        CancellationToken cancellationToken);
    public Task<CatalogMediaCommandResult> SaveAsync(
        CatalogMediaAsset asset,
        long expectedStoredAggregateRevision,
        CatalogMediaCommandIdentity commandIdentity,
        CatalogMediaCommandContext context,
        CatalogMediaOutboxMessage? outbox,
        CancellationToken cancellationToken);
    public Task<CatalogMediaAsset?> GetAsync(Guid assetId, CancellationToken cancellationToken);
    public Task<CatalogMediaProcessingLease?> TryLeaseUploadedAsync(
        string workerIdentity,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken);
    public Task CompleteProcessingAsync(
        CatalogMediaProcessingLease lease,
        CatalogMediaAsset asset,
        CatalogMediaOutboxMessage outbox,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
    public Task<int> RecordProcessingFailureAsync(
        CatalogMediaProcessingLease lease,
        string failure,
        bool terminal,
        int maximumAttempts,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);
}

public interface ICatalogMediaObjectStore
{
    public Task<CatalogMediaUploadAuthorization> CreateUploadAuthorizationAsync(
        CatalogMediaAsset asset,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
    public Task<CatalogMediaObjectDescriptor> VerifyUploadedAsync(
        CatalogMediaAsset asset,
        CancellationToken cancellationToken);
    public Task<Stream> OpenQuarantineReadAsync(CatalogMediaAsset asset, CancellationToken cancellationToken);
    public Task<CatalogMediaObjectDescriptor> PutVariantAsync(
        CatalogMediaAsset asset,
        CatalogMediaVariantKind kind,
        string contentType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
    public Task DeleteQuarantineAsync(CatalogMediaAsset asset, CancellationToken cancellationToken);
}

public interface ICatalogMediaScanner
{
    public Task<CatalogMediaScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

public interface ICatalogMediaVariantProcessor
{
    public Task<IReadOnlyList<CatalogMediaVariantContent>> CreateVariantsAsync(
        string sourceContentType,
        Stream source,
        CancellationToken cancellationToken);
}

public sealed class CatalogMediaCommandService(
    ICatalogMediaRepository repository,
    ICatalogMediaObjectStore objectStore,
    ICatalogMediaIdSource idSource,
    ICatalogMediaClock clock)
{
    public async Task<(CatalogMediaResponse Response, bool Replayed)> RegisterAsync(
        RegisterCatalogMediaRequest request,
        CatalogMediaCommandContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(request.ContractIdentity, CatalogMediaContractIdentity.CommandApi, StringComparison.Ordinal) ||
            request.ContractRevision != CatalogMediaContractIdentity.CommandApiRevision)
            throw Failure("CatalogMedia.Contracts", "CATALOG_MEDIA_CONTRACT_UNSUPPORTED", 422,
                "Catalog media contract identity or revision is unsupported.",
                "Use the generated current Catalog media client.");
        var digest = CatalogMediaCanonicalJson.ComputeDigest(request);
        var command = CatalogMediaCommandIdentity.Create("catalog-media.register", idempotencyKey, digest);
        var replay = await repository.ReadCommandResultAsync(command, cancellationToken);
        if (replay is not null) return (CatalogMediaMapper.ToResponse(replay.Asset), true);
        var id = idSource.CreateId();
        var catalog = request.CatalogKey.Trim().ToLowerInvariant();
        var objectKey = $"catalog-media/quarantine/{catalog}/{id:N}/original";
        var asset = CatalogMediaAsset.Register(
            id,
            catalog,
            objectKey,
            request.ContentType,
            request.ContentDigest,
            request.Size,
            CatalogMediaMapper.ToDomain(request.RightsBasis),
            request.RightsReference,
            clock.GetUtcNow());
        var result = await repository.AddAsync(asset, command, context, cancellationToken);
        return (CatalogMediaMapper.ToResponse(result.Asset), result.Replayed);
    }

    public async Task<(CatalogMediaUploadAuthorizationResponse Response, bool Replayed)> PrepareUploadAsync(
        Guid assetId,
        PrepareCatalogMediaUploadRequest request,
        CatalogMediaCommandContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var digest = CatalogMediaCanonicalJson.ComputeDigest(new { assetId, request });
        var command = CatalogMediaCommandIdentity.Create($"catalog-media.{assetId:N}.upload-authorize", idempotencyKey, digest);
        var replay = await repository.ReadCommandResultAsync(command, cancellationToken);
        if (replay is not null)
        {
            var expiresAtUtc = replay.Asset.UploadAuthorizationExpiresAtUtc
                ?? throw Failure(
                    "CatalogMedia.Commands",
                    "CATALOG_MEDIA_UPLOAD_REPLAY_CORRUPT",
                    500,
                    "Persisted upload authorization result has no expiry.",
                    "Restore the exact media command result from a verified backup.");
            var remaining = expiresAtUtc - clock.GetUtcNow();
            if (remaining < TimeSpan.FromSeconds(1))
            {
                throw Failure(
                    "CatalogMedia.Commands",
                    "CATALOG_MEDIA_UPLOAD_AUTHORIZATION_EXPIRED_REPLAY",
                    409,
                    "The replayed upload authorization has expired.",
                    "Submit a new upload-authorization command with a new Idempotency-Key.");
            }
            var replayAuthorization = await objectStore.CreateUploadAuthorizationAsync(
                replay.Asset,
                remaining,
                cancellationToken);
            return (new CatalogMediaUploadAuthorizationResponse(
                CatalogMediaMapper.ToResponse(replay.Asset), replayAuthorization.UploadUri,
                replayAuthorization.ExpiresAtUtc, replayAuthorization.RequiredHeaders), true);
        }
        var asset = await RequireAsync(assetId, cancellationToken);
        var storedRevision = asset.AggregateRevision;
        var now = clock.GetUtcNow();
        var lifetime = TimeSpan.FromSeconds(request.LifetimeSeconds);
        var authorization = await objectStore.CreateUploadAuthorizationAsync(asset, lifetime, cancellationToken);
        asset.AuthorizeUpload(request.ExpectedAggregateRevision, now, authorization.ExpiresAtUtc);
        var result = await repository.SaveAsync(asset, storedRevision, command, context, null, cancellationToken);
        return (new CatalogMediaUploadAuthorizationResponse(
            CatalogMediaMapper.ToResponse(result.Asset), authorization.UploadUri,
            authorization.ExpiresAtUtc, authorization.RequiredHeaders), result.Replayed);
    }

    public async Task<(CatalogMediaResponse Response, bool Replayed)> CompleteUploadAsync(
        Guid assetId,
        CompleteCatalogMediaUploadRequest request,
        CatalogMediaCommandContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var digest = CatalogMediaCanonicalJson.ComputeDigest(new { assetId, request });
        var command = CatalogMediaCommandIdentity.Create($"catalog-media.{assetId:N}.upload-complete", idempotencyKey, digest);
        var replay = await repository.ReadCommandResultAsync(command, cancellationToken);
        if (replay is not null) return (CatalogMediaMapper.ToResponse(replay.Asset), true);
        var asset = await RequireAsync(assetId, cancellationToken);
        var storedRevision = asset.AggregateRevision;
        var descriptor = await objectStore.VerifyUploadedAsync(asset, cancellationToken);
        asset.ConfirmUploaded(
            request.ExpectedAggregateRevision,
            descriptor.ObjectKey,
            descriptor.ContentType,
            descriptor.ContentDigest,
            descriptor.Size,
            clock.GetUtcNow());
        var result = await repository.SaveAsync(asset, storedRevision, command, context, null, cancellationToken);
        return (CatalogMediaMapper.ToResponse(result.Asset), result.Replayed);
    }

    public async Task<(CatalogMediaResponse Response, bool Replayed)> RevokeRightsAsync(
        Guid assetId,
        RevokeCatalogMediaRightsRequest request,
        CatalogMediaCommandContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var digest = CatalogMediaCanonicalJson.ComputeDigest(new { assetId, request });
        var command = CatalogMediaCommandIdentity.Create($"catalog-media.{assetId:N}.rights-revoke", idempotencyKey, digest);
        var replay = await repository.ReadCommandResultAsync(command, cancellationToken);
        if (replay is not null) return (CatalogMediaMapper.ToResponse(replay.Asset), true);
        var asset = await RequireAsync(assetId, cancellationToken);
        var storedRevision = asset.AggregateRevision;
        var occurredAtUtc = clock.GetUtcNow();
        asset.RevokeRights(request.ExpectedAggregateRevision, context.Actor.Id, request.Reason, occurredAtUtc);
        var eventId = idSource.CreateId();
        var integrationEvent = new CatalogMediaRightsRevoked(
            eventId, asset.Id, asset.CatalogKey, asset.AggregateRevision, occurredAtUtc);
        var outbox = CatalogMediaCanonicalJson.ToOutbox(
            eventId,
            CatalogMediaIntegrationEventTypes.RightsRevoked,
            CatalogMediaIntegrationEventContracts.RightsRevoked,
            integrationEvent,
            occurredAtUtc,
            context);
        var result = await repository.SaveAsync(asset, storedRevision, command, context, outbox, cancellationToken);
        return (CatalogMediaMapper.ToResponse(result.Asset), result.Replayed);
    }

    public async Task<CatalogMediaResponse> GetAsync(Guid assetId, CancellationToken cancellationToken) =>
        CatalogMediaMapper.ToResponse(await RequireAsync(assetId, cancellationToken));

    private async Task<CatalogMediaAsset> RequireAsync(Guid assetId, CancellationToken cancellationToken)
    {
        if (assetId == Guid.Empty) throw Failure("CatalogMedia.Assets", "CATALOG_MEDIA_ID_REQUIRED", 400,
            "Catalog media asset ID is required.", "Use the exact media asset ID returned by registration.");
        return await repository.GetAsync(assetId, cancellationToken)
            ?? throw Failure("CatalogMedia.Assets", "CATALOG_MEDIA_NOT_FOUND", 404,
                $"Catalog media asset '{assetId}' was not found.",
                "Reload the exact media asset before submitting another command.");
    }

    private static CatalogMediaApplicationException Failure(
        string owner, string code, int status, string message, string action) =>
        new(owner, code, status, message, action);
}

public sealed class CatalogMediaProcessingService(
    ICatalogMediaRepository repository,
    ICatalogMediaObjectStore objectStore,
    ICatalogMediaScanner scanner,
    ICatalogMediaVariantProcessor variantProcessor,
    ICatalogMediaIdSource idSource,
    ICatalogMediaClock clock)
{
    public async Task<bool> ProcessOneAsync(
        string workerIdentity,
        Guid systemActorId,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerIdentity);
        if (systemActorId == Guid.Empty)
        {
            throw new CatalogMediaApplicationException(
                "CatalogMedia.Processing",
                "CATALOG_MEDIA_SYSTEM_ACTOR_REQUIRED",
                500,
                "Catalog media processing requires a registered system actor.",
                "Configure CatalogMediaWorker:SystemActorId with a non-empty actor ID.");
        }
        var lease = await repository.TryLeaseUploadedAsync(
            workerIdentity, clock.GetUtcNow(), leaseDuration, maximumAttempts, cancellationToken);
        if (lease is null) return false;
        try
        {
            await using var scanStream = await objectStore.OpenQuarantineReadAsync(lease.Asset, cancellationToken);
            var scan = await scanner.ScanAsync(scanStream, cancellationToken);
            var asset = lease.Asset;
            if (asset.State != CatalogMediaState.Scanning)
            {
                throw new CatalogMediaApplicationException(
                    "CatalogMedia.Processing",
                    "CATALOG_MEDIA_LEASE_STATE_INVALID",
                    500,
                    "A media processing lease must own an asset already transitioned to scanning.",
                    "Correct the repository lease transaction before processing the object.");
            }
            var now = clock.GetUtcNow();
            if (!scan.IsClean)
            {
                asset.Reject(asset.AggregateRevision, "malware-detected", now);
                var rejectionEventId = idSource.CreateId();
                var rejection = new CatalogMediaRejected(
                    rejectionEventId,
                    asset.Id,
                    asset.CatalogKey,
                    asset.AggregateRevision,
                    asset.FailureCode ?? "malware-detected",
                    now);
                var rejectionOutbox = CatalogMediaCanonicalJson.ToOutbox(
                    rejectionEventId,
                    CatalogMediaIntegrationEventTypes.Rejected,
                    CatalogMediaIntegrationEventContracts.Rejected,
                    rejection,
                    now,
                    CatalogMediaCommandContext.Start(CatalogMediaActor.Create(systemActorId), workerIdentity));
                await repository.CompleteProcessingAsync(lease, asset, rejectionOutbox, now, cancellationToken);
                await objectStore.DeleteQuarantineAsync(asset, cancellationToken);
                return true;
            }
            await using var source = await objectStore.OpenQuarantineReadAsync(asset, cancellationToken);
            var contents = await variantProcessor.CreateVariantsAsync(asset.ExpectedContentType, source, cancellationToken);
            var variants = new List<CatalogMediaVariant>(contents.Count);
            foreach (var content in contents.OrderBy(item => item.Kind))
            {
                var descriptor = await objectStore.PutVariantAsync(
                    asset, content.Kind, content.ContentType, content.Content, cancellationToken);
                variants.Add(CatalogMediaVariant.Create(
                    idSource.CreateId(), asset.Id, content.Kind, descriptor.ObjectKey,
                    descriptor.ContentType, descriptor.ContentDigest, descriptor.Size,
                    content.Width, content.Height, now));
            }
            asset.Accept(asset.AggregateRevision, variants, now);
            var eventId = idSource.CreateId();
            var integrationEvent = new CatalogMediaAccepted(
                eventId, asset.Id, asset.CatalogKey, asset.AggregateRevision,
                CatalogMediaMapper.ToResponse(asset).Variants, now);
            var context = CatalogMediaCommandContext.Start(
                CatalogMediaActor.Create(systemActorId),
                workerIdentity);
            var outbox = CatalogMediaCanonicalJson.ToOutbox(
                eventId, CatalogMediaIntegrationEventTypes.Accepted,
                CatalogMediaIntegrationEventContracts.Accepted, integrationEvent, now, context);
            await repository.CompleteProcessingAsync(lease, asset, outbox, now, cancellationToken);
            await objectStore.DeleteQuarantineAsync(asset, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _ = await repository.RecordProcessingFailureAsync(
                lease, exception.Message, terminal: false, maximumAttempts,
                clock.GetUtcNow(), cancellationToken);
            return true;
        }
    }
}

public static class CatalogMediaMapper
{
    public static CatalogMediaRightsBasis ToDomain(CatalogMediaRightsBasisContract value) => value switch
    {
        CatalogMediaRightsBasisContract.OwnerProvided => CatalogMediaRightsBasis.OwnerProvided,
        CatalogMediaRightsBasisContract.Licensed => CatalogMediaRightsBasis.Licensed,
        CatalogMediaRightsBasisContract.PublicDomain => CatalogMediaRightsBasis.PublicDomain,
        _ => throw new CatalogMediaApplicationException(
            "CatalogMedia.Contracts", "CATALOG_MEDIA_RIGHTS_BASIS_UNSUPPORTED", 400,
            "Catalog media rights basis is unsupported.", "Use a declared string enum token."),
    };

    public static CatalogMediaResponse ToResponse(CatalogMediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new CatalogMediaResponse(
            asset.Id,
            asset.CatalogKey,
            (CatalogMediaStateContract)asset.State,
            asset.QuarantineObjectKey,
            asset.ExpectedContentType,
            asset.ExpectedContentDigest,
            asset.ExpectedSize,
            (CatalogMediaRightsBasisContract)asset.RightsBasis,
            asset.RightsReference,
            asset.RegisteredAtUtc,
            asset.ChangedAtUtc,
            asset.AggregateRevision,
            asset.UploadAuthorizationExpiresAtUtc,
            asset.UploadedAtUtc,
            asset.ScannedAtUtc,
            asset.AcceptedAtUtc,
            asset.RightsRevokedAtUtc,
            asset.FailureCode,
            asset.Variants.Select(item => new CatalogMediaVariantResponse(
                item.Id,
                (CatalogMediaVariantKindContract)item.Kind,
                item.ObjectKey,
                item.ContentType,
                item.ContentDigest,
                item.Size,
                item.Width,
                item.Height,
                item.CreatedAtUtc)).ToArray());
    }
}

public static class CatalogMediaCanonicalJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = JsonSerializer.SerializeToElement(value, Options);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(element, writer);
        }
        return buffer.ToArray();
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> document)
    {
        var value = JsonSerializer.Deserialize<T>(document, Options);
        return value ?? throw new CatalogMediaApplicationException(
            "CatalogMedia.Contracts",
            "CATALOG_MEDIA_DOCUMENT_EMPTY",
            500,
            "Catalog media document deserialized to no value.",
            "Restore the exact document from a verified owner source.");
    }

    public static string ComputeDigest<T>(T value) => ComputeDigest(Serialize(value).AsSpan());

    public static string ComputeDigest(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    public static void RequireDigest(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Digest must be lowercase SHA-256.", parameterName);
    }

    public static CatalogMediaOutboxMessage ToOutbox<T>(
        Guid eventId,
        string routingKey,
        string contractIdentity,
        T payload,
        DateTimeOffset occurredAtUtc,
        CatalogMediaCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bytes = Serialize(payload);
        return new CatalogMediaOutboxMessage(
            eventId,
            routingKey,
            contractIdentity,
            Encoding.UTF8.GetString(bytes),
            ComputeDigest(bytes.AsSpan()),
            occurredAtUtc,
            context.CorrelationId,
            context.CausationId);
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name.Normalize(NormalizationForm.FormC));
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString()!.Normalize(NormalizationForm.FormC));
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"JSON value kind '{element.ValueKind}' cannot be canonicalized.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

public sealed class CatalogMediaApplicationException(
    string owner,
    string code,
    int statusCode,
    string message,
    string requiredAction,
    IReadOnlyDictionary<string, object?>? context = null,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Owner { get; } = owner;
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string RequiredAction { get; } = requiredAction;
    public IReadOnlyDictionary<string, object?> Context { get; } =
        context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}
