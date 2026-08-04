#!/usr/bin/env python3
from __future__ import annotations

import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE_ROOT = ROOT / "src" / "CatalogMedia"
CATALOG_ROOT = ROOT / "src" / "Catalog"
TARGETS = {
    "CatalogMedia.Domain": "Catalog.Media.Domain",
    "CatalogMedia.Contracts": "Catalog.Media.Contracts",
    "CatalogMedia.Application": "Catalog.Media.Application",
}


def replace_required(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"Catalog media hardening anchor is missing: {label}")
    return text.replace(old, new)


for source_name, target_name in TARGETS.items():
    source = SOURCE_ROOT / source_name
    target = CATALOG_ROOT / target_name
    if not source.exists():
        raise RuntimeError(f"Generated Catalog media project is missing: {source}")
    if target.exists():
        shutil.rmtree(target)
    shutil.move(str(source), str(target))
    old_project = target / f"{source_name}.csproj"
    new_project = target / f"{target_name}.csproj"
    old_project.rename(new_project)

if SOURCE_ROOT.exists():
    shutil.rmtree(SOURCE_ROOT)

application_project = CATALOG_ROOT / "Catalog.Media.Application" / "Catalog.Media.Application.csproj"
project = application_project.read_text(encoding="utf-8")
project = project.replace(
    "../CatalogMedia.Domain/CatalogMedia.Domain.csproj",
    "../Catalog.Media.Domain/Catalog.Media.Domain.csproj",
).replace(
    "../CatalogMedia.Contracts/CatalogMedia.Contracts.csproj",
    "../Catalog.Media.Contracts/Catalog.Media.Contracts.csproj",
)
application_project.write_text(project, encoding="utf-8")

contracts_path = CATALOG_ROOT / "Catalog.Media.Contracts" / "CatalogMediaContracts.cs"
contracts = contracts_path.read_text(encoding="utf-8")
contracts = replace_required(
    contracts,
    '''public static class CatalogMediaIntegrationEventTypes
{
    public const string Accepted = "catalog.media.accepted";
    public const string RightsRevoked = "catalog.media.rights-revoked";
}''',
    '''public static class CatalogMediaIntegrationEventTypes
{
    public const string Accepted = "catalog.media.accepted";
    public const string Rejected = "catalog.media.rejected";
    public const string RightsRevoked = "catalog.media.rights-revoked";
}''',
    "event routing identities",
)
contracts = replace_required(
    contracts,
    '''public static class CatalogMediaIntegrationEventContracts
{
    public const string Accepted = "aggregator.catalog-media.accepted@1";
    public const string RightsRevoked = "aggregator.catalog-media.rights-revoked@1";
}''',
    '''public static class CatalogMediaIntegrationEventContracts
{
    public const string Accepted = "aggregator.catalog-media.accepted@1";
    public const string Rejected = "aggregator.catalog-media.rejected@1";
    public const string RightsRevoked = "aggregator.catalog-media.rights-revoked@1";
}''',
    "event contract identities",
)
contracts = replace_required(
    contracts,
    '''public sealed record CatalogMediaRightsRevoked(
    Guid EventId,
    Guid AssetId,
    string CatalogKey,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);''',
    '''public sealed record CatalogMediaRejected(
    Guid EventId,
    Guid AssetId,
    string CatalogKey,
    long AggregateRevision,
    string FailureCode,
    DateTimeOffset OccurredAtUtc);

public sealed record CatalogMediaRightsRevoked(
    Guid EventId,
    Guid AssetId,
    string CatalogKey,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);''',
    "rejected event contract",
)
contracts_path.write_text(contracts, encoding="utf-8")

application_path = CATALOG_ROOT / "Catalog.Media.Application" / "CatalogMediaApplication.cs"
application = application_path.read_text(encoding="utf-8")
application = replace_required(
    application,
    '''        var replay = await repository.ReadCommandResultAsync(command, cancellationToken);
        if (replay is not null)
        {
            var replayAuthorization = await objectStore.CreateUploadAuthorizationAsync(
                replay.Asset,
                replay.Asset.UploadAuthorizationExpiresAtUtc!.Value - clock.GetUtcNow(),
                cancellationToken);
            return (new CatalogMediaUploadAuthorizationResponse(
                CatalogMediaMapper.ToResponse(replay.Asset), replayAuthorization.UploadUri,
                replayAuthorization.ExpiresAtUtc, replayAuthorization.RequiredHeaders), true);
        }''',
    '''        var replay = await repository.ReadCommandResultAsync(command, cancellationToken);
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
        }''',
    "expired upload replay",
)
application = application.replace(
    '''        RegisterCatalogMediaRequest request,
        CatalogMediaCommandContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);''',
    '''        RegisterCatalogMediaRequest request,
        CatalogMediaCommandContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);''',
)
for request_type in (
    "PrepareCatalogMediaUploadRequest",
    "CompleteCatalogMediaUploadRequest",
    "RevokeCatalogMediaRightsRequest",
):
    anchor = f'''        {request_type} request,
        CatalogMediaCommandContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {{
        ArgumentNullException.ThrowIfNull(request);'''
    replacement = f'''        {request_type} request,
        CatalogMediaCommandContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {{
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);'''
    application = replace_required(application, anchor, replacement, f"{request_type} context guard")
application = replace_required(
    application,
    '''    public async Task<bool> ProcessOneAsync(
        string workerIdentity,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        var lease = await repository.TryLeaseUploadedAsync(''',
    '''    public async Task<bool> ProcessOneAsync(
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
        var lease = await repository.TryLeaseUploadedAsync(''',
    "processing system actor",
)
application = replace_required(
    application,
    '''            var asset = lease.Asset;
            var storedRevision = asset.AggregateRevision;
            var now = clock.GetUtcNow();''',
    '''            var asset = lease.Asset;
            if (asset.State != CatalogMediaState.Scanning)
            {
                throw new CatalogMediaApplicationException(
                    "CatalogMedia.Processing",
                    "CATALOG_MEDIA_LEASE_STATE_INVALID",
                    500,
                    "A media processing lease must own an asset already transitioned to scanning.",
                    "Correct the repository lease transaction before processing the object.");
            }
            var now = clock.GetUtcNow();''',
    "scan-state proof",
)
application = replace_required(
    application,
    '''                var rejectionEventId = idSource.CreateId();
                var rejection = new CatalogMediaRightsRevoked(
                    rejectionEventId, asset.Id, asset.CatalogKey, asset.AggregateRevision, now);
                var rejectionOutbox = CatalogMediaCanonicalJson.ToOutbox(
                    rejectionEventId, "catalog.media.rejected", "aggregator.catalog-media.rejected@1",
                    rejection, now, CatalogMediaCommandContext.Start(CatalogMediaActor.Create(idSource.CreateId()), workerIdentity));''',
    '''                var rejectionEventId = idSource.CreateId();
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
                    CatalogMediaCommandContext.Start(CatalogMediaActor.Create(systemActorId), workerIdentity));''',
    "typed rejection event",
)
application = replace_required(
    application,
    '''            var context = CatalogMediaCommandContext.Start(CatalogMediaActor.Create(idSource.CreateId()), workerIdentity);''',
    '''            var context = CatalogMediaCommandContext.Start(
                CatalogMediaActor.Create(systemActorId),
                workerIdentity);''',
    "accepted event actor",
)
application = replace_required(
    application,
    '''    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }''',
    '''    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = JsonSerializer.SerializeToElement(value, Options);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(element, writer);
        }
        return buffer.ToArray();
    }''',
    "canonical serializer entry",
)
application = replace_required(
    application,
    '''    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }''',
    '''    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
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
    }''',
    "canonical writer",
)
application_path.write_text(application, encoding="utf-8")

extension_path = CATALOG_ROOT / "Catalog.Media.Application" / "CatalogMediaApplicationServiceCollectionExtensions.cs"
extension_path.write_text(
    '''using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.CatalogMedia.Application;

public static class CatalogMediaApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogMediaApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<CatalogMediaCommandService>();
        services.AddScoped<CatalogMediaProcessingService>();
        return services;
    }
}
''',
    encoding="utf-8",
)

print("Catalog media generated source hardened and moved under src/Catalog.")
