#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / "src" / "CatalogMedia"
DOMAIN = BASE / "CatalogMedia.Domain"
CONTRACTS = BASE / "CatalogMedia.Contracts"
APPLICATION = BASE / "CatalogMedia.Application"


def write(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content.rstrip() + "\n", encoding="utf-8")


write(DOMAIN / "CatalogMedia.Domain.csproj", '<Project Sdk="Microsoft.NET.Sdk" />')
write(
    DOMAIN / "CatalogMediaAsset.cs",
    r'''
using System.Collections.ObjectModel;

namespace Aggregator.CatalogMedia.Domain;

public enum CatalogMediaState
{
    Registered = 1,
    UploadAuthorized = 2,
    Uploaded = 3,
    Scanning = 4,
    Accepted = 5,
    Rejected = 6,
    RightsRevoked = 7,
    Archived = 8,
}

public enum CatalogMediaRightsBasis
{
    OwnerProvided = 1,
    Licensed = 2,
    PublicDomain = 3,
}

public enum CatalogMediaVariantKind
{
    Original = 1,
    Thumbnail = 2,
    Card = 3,
    Gallery = 4,
}

public sealed record CatalogMediaVariant
{
    private CatalogMediaVariant(
        Guid id,
        Guid assetId,
        CatalogMediaVariantKind kind,
        string objectKey,
        string contentType,
        string contentDigest,
        long size,
        int width,
        int height,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        AssetId = assetId;
        Kind = kind;
        ObjectKey = objectKey;
        ContentType = contentType;
        ContentDigest = contentDigest;
        Size = size;
        Width = width;
        Height = height;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }
    public Guid AssetId { get; }
    public CatalogMediaVariantKind Kind { get; }
    public string ObjectKey { get; }
    public string ContentType { get; }
    public string ContentDigest { get; }
    public long Size { get; }
    public int Width { get; }
    public int Height { get; }
    public DateTimeOffset CreatedAtUtc { get; }

    public static CatalogMediaVariant Create(
        Guid id,
        Guid assetId,
        CatalogMediaVariantKind kind,
        string objectKey,
        string contentType,
        string contentDigest,
        long size,
        int width,
        int height,
        DateTimeOffset createdAtUtc)
    {
        CatalogMediaRules.RequireId(id, nameof(id));
        CatalogMediaRules.RequireId(assetId, nameof(assetId));
        if (!Enum.IsDefined(kind))
        {
            throw new CatalogMediaDomainException("CATALOG_MEDIA_VARIANT_KIND_INVALID", "Media variant kind is unsupported.");
        }
        CatalogMediaRules.RequireObjectKey(objectKey, "catalog-media/published/", nameof(objectKey));
        CatalogMediaRules.RequireContentType(contentType, nameof(contentType));
        CatalogMediaRules.RequireDigest(contentDigest, nameof(contentDigest));
        if (size is < 1 or > CatalogMediaRules.MaximumObjectBytes)
        {
            throw new CatalogMediaDomainException("CATALOG_MEDIA_VARIANT_SIZE_INVALID", "Media variant size is outside the accepted bounds.");
        }
        if (width is < 1 or > 20000 || height is < 1 or > 20000)
        {
            throw new CatalogMediaDomainException("CATALOG_MEDIA_VARIANT_DIMENSIONS_INVALID", "Media variant dimensions are outside the accepted bounds.");
        }
        CatalogMediaRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        return new CatalogMediaVariant(
            id,
            assetId,
            kind,
            objectKey,
            contentType,
            contentDigest,
            size,
            width,
            height,
            createdAtUtc);
    }
}

public sealed class CatalogMediaAsset
{
    private readonly List<CatalogMediaVariant> _variants;

    private CatalogMediaAsset(
        Guid id,
        string catalogKey,
        CatalogMediaState state,
        string quarantineObjectKey,
        string expectedContentType,
        string expectedContentDigest,
        long expectedSize,
        CatalogMediaRightsBasis rightsBasis,
        string rightsReference,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset changedAtUtc,
        long aggregateRevision,
        DateTimeOffset? uploadAuthorizationExpiresAtUtc,
        DateTimeOffset? uploadedAtUtc,
        DateTimeOffset? scannedAtUtc,
        DateTimeOffset? acceptedAtUtc,
        DateTimeOffset? rightsRevokedAtUtc,
        Guid? rightsRevokedByActorId,
        string? failureCode,
        IEnumerable<CatalogMediaVariant> variants)
    {
        Id = id;
        CatalogKey = catalogKey;
        State = state;
        QuarantineObjectKey = quarantineObjectKey;
        ExpectedContentType = expectedContentType;
        ExpectedContentDigest = expectedContentDigest;
        ExpectedSize = expectedSize;
        RightsBasis = rightsBasis;
        RightsReference = rightsReference;
        RegisteredAtUtc = registeredAtUtc;
        ChangedAtUtc = changedAtUtc;
        AggregateRevision = aggregateRevision;
        UploadAuthorizationExpiresAtUtc = uploadAuthorizationExpiresAtUtc;
        UploadedAtUtc = uploadedAtUtc;
        ScannedAtUtc = scannedAtUtc;
        AcceptedAtUtc = acceptedAtUtc;
        RightsRevokedAtUtc = rightsRevokedAtUtc;
        RightsRevokedByActorId = rightsRevokedByActorId;
        FailureCode = failureCode;
        _variants = variants.OrderBy(item => item.Kind).ToList();
    }

    public Guid Id { get; }
    public string CatalogKey { get; }
    public CatalogMediaState State { get; private set; }
    public string QuarantineObjectKey { get; }
    public string ExpectedContentType { get; }
    public string ExpectedContentDigest { get; }
    public long ExpectedSize { get; }
    public CatalogMediaRightsBasis RightsBasis { get; }
    public string RightsReference { get; private set; }
    public DateTimeOffset RegisteredAtUtc { get; }
    public DateTimeOffset ChangedAtUtc { get; private set; }
    public long AggregateRevision { get; private set; }
    public DateTimeOffset? UploadAuthorizationExpiresAtUtc { get; private set; }
    public DateTimeOffset? UploadedAtUtc { get; private set; }
    public DateTimeOffset? ScannedAtUtc { get; private set; }
    public DateTimeOffset? AcceptedAtUtc { get; private set; }
    public DateTimeOffset? RightsRevokedAtUtc { get; private set; }
    public Guid? RightsRevokedByActorId { get; private set; }
    public string? FailureCode { get; private set; }
    public IReadOnlyList<CatalogMediaVariant> Variants => new ReadOnlyCollection<CatalogMediaVariant>(_variants);

    public static CatalogMediaAsset Register(
        Guid id,
        string catalogKey,
        string quarantineObjectKey,
        string expectedContentType,
        string expectedContentDigest,
        long expectedSize,
        CatalogMediaRightsBasis rightsBasis,
        string rightsReference,
        DateTimeOffset registeredAtUtc)
    {
        CatalogMediaRules.RequireId(id, nameof(id));
        var normalizedCatalog = CatalogMediaRules.RequireKey(catalogKey, nameof(catalogKey));
        CatalogMediaRules.RequireObjectKey(quarantineObjectKey, $"catalog-media/quarantine/{normalizedCatalog}/", nameof(quarantineObjectKey));
        CatalogMediaRules.RequireContentType(expectedContentType, nameof(expectedContentType));
        CatalogMediaRules.RequireDigest(expectedContentDigest, nameof(expectedContentDigest));
        if (expectedSize is < 1 or > CatalogMediaRules.MaximumObjectBytes)
        {
            throw new CatalogMediaDomainException("CATALOG_MEDIA_SIZE_INVALID", "Expected media size is outside the accepted bounds.");
        }
        if (!Enum.IsDefined(rightsBasis))
        {
            throw new CatalogMediaDomainException("CATALOG_MEDIA_RIGHTS_BASIS_INVALID", "Media rights basis is unsupported.");
        }
        var normalizedRights = CatalogMediaRules.RequireText(rightsReference, nameof(rightsReference), 2000);
        CatalogMediaRules.RequireUtc(registeredAtUtc, nameof(registeredAtUtc));
        return new CatalogMediaAsset(
            id,
            normalizedCatalog,
            CatalogMediaState.Registered,
            quarantineObjectKey,
            expectedContentType,
            expectedContentDigest,
            expectedSize,
            rightsBasis,
            normalizedRights,
            registeredAtUtc,
            registeredAtUtc,
            1,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);
    }

    public void AuthorizeUpload(
        long expectedAggregateRevision,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc)
    {
        RequireRevision(expectedAggregateRevision);
        CatalogMediaRules.RequireUtc(nowUtc, nameof(nowUtc));
        CatalogMediaRules.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (State is not (CatalogMediaState.Registered or CatalogMediaState.UploadAuthorized))
        {
            throw InvalidTransition(CatalogMediaState.UploadAuthorized);
        }
        var lifetime = expiresAtUtc - nowUtc;
        if (lifetime < TimeSpan.FromMinutes(1) || lifetime > TimeSpan.FromMinutes(15))
        {
            throw new CatalogMediaDomainException(
                "CATALOG_MEDIA_UPLOAD_LIFETIME_INVALID",
                "Media upload authorization lifetime must be between one and fifteen minutes.");
        }
        State = CatalogMediaState.UploadAuthorized;
        UploadAuthorizationExpiresAtUtc = expiresAtUtc;
        Advance(nowUtc);
    }

    public void ConfirmUploaded(
        long expectedAggregateRevision,
        string actualObjectKey,
        string actualContentType,
        string actualContentDigest,
        long actualSize,
        DateTimeOffset uploadedAtUtc)
    {
        RequireRevision(expectedAggregateRevision);
        CatalogMediaRules.RequireUtc(uploadedAtUtc, nameof(uploadedAtUtc));
        if (State != CatalogMediaState.UploadAuthorized)
        {
            throw InvalidTransition(CatalogMediaState.Uploaded);
        }
        if (UploadAuthorizationExpiresAtUtc is null || uploadedAtUtc > UploadAuthorizationExpiresAtUtc)
        {
            throw new CatalogMediaDomainException("CATALOG_MEDIA_UPLOAD_AUTHORIZATION_EXPIRED", "Media upload authorization has expired.");
        }
        if (!string.Equals(actualObjectKey, QuarantineObjectKey, StringComparison.Ordinal) ||
            !string.Equals(actualContentType, ExpectedContentType, StringComparison.Ordinal) ||
            !string.Equals(actualContentDigest, ExpectedContentDigest, StringComparison.Ordinal) ||
            actualSize != ExpectedSize)
        {
            throw new CatalogMediaDomainException(
                "CATALOG_MEDIA_UPLOAD_IDENTITY_MISMATCH",
                "Uploaded media metadata does not match the registered object identity.");
        }
        State = CatalogMediaState.Uploaded;
        UploadedAtUtc = uploadedAtUtc;
        Advance(uploadedAtUtc);
    }

    public void StartScan(long expectedAggregateRevision, DateTimeOffset startedAtUtc)
    {
        RequireRevision(expectedAggregateRevision);
        CatalogMediaRules.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        if (State != CatalogMediaState.Uploaded)
        {
            throw InvalidTransition(CatalogMediaState.Scanning);
        }
        State = CatalogMediaState.Scanning;
        FailureCode = null;
        Advance(startedAtUtc);
    }

    public void Accept(
        long expectedAggregateRevision,
        IEnumerable<CatalogMediaVariant> variants,
        DateTimeOffset acceptedAtUtc)
    {
        RequireRevision(expectedAggregateRevision);
        ArgumentNullException.ThrowIfNull(variants);
        CatalogMediaRules.RequireUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        if (State != CatalogMediaState.Scanning)
        {
            throw InvalidTransition(CatalogMediaState.Accepted);
        }
        var materialized = variants.OrderBy(item => item.Kind).ToArray();
        if (materialized.Length == 0 || materialized.Any(item => item.AssetId != Id))
        {
            throw new CatalogMediaDomainException("CATALOG_MEDIA_VARIANTS_INVALID", "Accepted variants must belong to the exact media asset.");
        }
        if (materialized.GroupBy(item => item.Kind).Any(group => group.Count() > 1) ||
            materialized.GroupBy(item => item.ObjectKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new CatalogMediaDomainException("CATALOG_MEDIA_VARIANTS_DUPLICATE", "Media variants must have unique kinds and object keys.");
        }
        if (!materialized.Any(item => item.Kind == CatalogMediaVariantKind.Original) ||
            !materialized.Any(item => item.Kind == CatalogMediaVariantKind.Thumbnail))
        {
            throw new CatalogMediaDomainException(
                "CATALOG_MEDIA_REQUIRED_VARIANTS_MISSING",
                "Accepted media requires exact original and thumbnail variants.");
        }
        _variants.Clear();
        _variants.AddRange(materialized);
        State = CatalogMediaState.Accepted;
        ScannedAtUtc = acceptedAtUtc;
        AcceptedAtUtc = acceptedAtUtc;
        FailureCode = null;
        Advance(acceptedAtUtc);
    }

    public void Reject(long expectedAggregateRevision, string failureCode, DateTimeOffset rejectedAtUtc)
    {
        RequireRevision(expectedAggregateRevision);
        CatalogMediaRules.RequireUtc(rejectedAtUtc, nameof(rejectedAtUtc));
        if (State != CatalogMediaState.Scanning)
        {
            throw InvalidTransition(CatalogMediaState.Rejected);
        }
        State = CatalogMediaState.Rejected;
        ScannedAtUtc = rejectedAtUtc;
        FailureCode = CatalogMediaRules.RequireKey(failureCode, nameof(failureCode));
        Advance(rejectedAtUtc);
    }

    public void RevokeRights(
        long expectedAggregateRevision,
        Guid actorId,
        string reason,
        DateTimeOffset revokedAtUtc)
    {
        RequireRevision(expectedAggregateRevision);
        CatalogMediaRules.RequireId(actorId, nameof(actorId));
        CatalogMediaRules.RequireText(reason, nameof(reason), 2000);
        CatalogMediaRules.RequireUtc(revokedAtUtc, nameof(revokedAtUtc));
        if (State != CatalogMediaState.Accepted)
        {
            throw InvalidTransition(CatalogMediaState.RightsRevoked);
        }
        State = CatalogMediaState.RightsRevoked;
        RightsReference = $"{RightsReference}\nrevoked: {reason.Trim()}";
        RightsRevokedAtUtc = revokedAtUtc;
        RightsRevokedByActorId = actorId;
        Advance(revokedAtUtc);
    }

    public static CatalogMediaAsset Restore(
        Guid id,
        string catalogKey,
        CatalogMediaState state,
        string quarantineObjectKey,
        string expectedContentType,
        string expectedContentDigest,
        long expectedSize,
        CatalogMediaRightsBasis rightsBasis,
        string rightsReference,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset changedAtUtc,
        long aggregateRevision,
        DateTimeOffset? uploadAuthorizationExpiresAtUtc,
        DateTimeOffset? uploadedAtUtc,
        DateTimeOffset? scannedAtUtc,
        DateTimeOffset? acceptedAtUtc,
        DateTimeOffset? rightsRevokedAtUtc,
        Guid? rightsRevokedByActorId,
        string? failureCode,
        IEnumerable<CatalogMediaVariant> variants)
    {
        var asset = Register(
            id,
            catalogKey,
            quarantineObjectKey,
            expectedContentType,
            expectedContentDigest,
            expectedSize,
            rightsBasis,
            rightsReference,
            registeredAtUtc);
        if (!Enum.IsDefined(state))
            throw new CatalogMediaDomainException("CATALOG_MEDIA_STATE_INVALID", "Stored media state is unsupported.");
        CatalogMediaRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < registeredAtUtc || aggregateRevision < 1)
            throw new CatalogMediaDomainException("CATALOG_MEDIA_HISTORY_INVALID", "Stored media history is inconsistent.");
        return new CatalogMediaAsset(
            asset.Id,
            asset.CatalogKey,
            state,
            asset.QuarantineObjectKey,
            asset.ExpectedContentType,
            asset.ExpectedContentDigest,
            asset.ExpectedSize,
            asset.RightsBasis,
            asset.RightsReference,
            registeredAtUtc,
            changedAtUtc,
            aggregateRevision,
            uploadAuthorizationExpiresAtUtc,
            uploadedAtUtc,
            scannedAtUtc,
            acceptedAtUtc,
            rightsRevokedAtUtc,
            rightsRevokedByActorId,
            failureCode,
            variants);
    }

    private void RequireRevision(long expectedAggregateRevision)
    {
        if (expectedAggregateRevision != AggregateRevision)
        {
            throw new CatalogMediaDomainException(
                "CATALOG_MEDIA_REVISION_CONFLICT",
                $"Expected media aggregate revision '{expectedAggregateRevision}', actual '{AggregateRevision}'.");
        }
    }

    private void Advance(DateTimeOffset changedAtUtc)
    {
        if (changedAtUtc < ChangedAtUtc)
            throw new CatalogMediaDomainException("CATALOG_MEDIA_TIME_REGRESSION", "Media transition time cannot regress.");
        ChangedAtUtc = changedAtUtc;
        AggregateRevision++;
    }

    private CatalogMediaDomainException InvalidTransition(CatalogMediaState target) =>
        new("CATALOG_MEDIA_TRANSITION_INVALID", $"Media asset cannot transition from '{State}' to '{target}'.");
}

public sealed class CatalogMediaDomainException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("Media domain error code is required.", nameof(code))
        : code;
}

internal static class CatalogMediaRules
{
    public const long MaximumObjectBytes = 100L * 1024L * 1024L;
    private static readonly HashSet<string> ContentTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    public static void RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier is required.", parameterName);
    }

    public static string RequireKey(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 120 || normalized.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Key contains unsupported characters.", parameterName);
        return normalized;
    }

    public static string RequireText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
            throw new ArgumentException("Text is invalid.", parameterName);
        return normalized;
    }

    public static void RequireDigest(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Digest must be lowercase SHA-256.", parameterName);
    }

    public static void RequireContentType(string value, string parameterName)
    {
        if (!ContentTypes.Contains(value)) throw new ArgumentException("Content type is unsupported.", parameterName);
    }

    public static void RequireObjectKey(string value, string prefix, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal) ||
            value.StartsWith('/') || value.Contains('\\') || value.Any(char.IsControl))
            throw new ArgumentException("Object key is outside the owner namespace.", parameterName);
    }

    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.", parameterName);
    }
}
''')

write(CONTRACTS / "CatalogMedia.Contracts.csproj", '<Project Sdk="Microsoft.NET.Sdk" />')
write(
    CONTRACTS / "CatalogMediaContracts.cs",
    r'''
namespace Aggregator.CatalogMedia.Contracts;

public static class CatalogMediaContractIdentity
{
    public const string CommandApi = "aggregator-catalog-media";
    public const int CommandApiRevision = 1;
}

public enum CatalogMediaStateContract
{
    Registered = 1,
    UploadAuthorized = 2,
    Uploaded = 3,
    Scanning = 4,
    Accepted = 5,
    Rejected = 6,
    RightsRevoked = 7,
    Archived = 8,
}

public enum CatalogMediaRightsBasisContract
{
    OwnerProvided = 1,
    Licensed = 2,
    PublicDomain = 3,
}

public enum CatalogMediaVariantKindContract
{
    Original = 1,
    Thumbnail = 2,
    Card = 3,
    Gallery = 4,
}

public sealed record RegisterCatalogMediaRequest(
    string ContractIdentity,
    int ContractRevision,
    string CatalogKey,
    string ContentType,
    string ContentDigest,
    long Size,
    CatalogMediaRightsBasisContract RightsBasis,
    string RightsReference);

public sealed record PrepareCatalogMediaUploadRequest(
    long ExpectedAggregateRevision,
    int LifetimeSeconds);

public sealed record CompleteCatalogMediaUploadRequest(long ExpectedAggregateRevision);

public sealed record RevokeCatalogMediaRightsRequest(
    long ExpectedAggregateRevision,
    string Reason);

public sealed record CatalogMediaVariantResponse(
    Guid Id,
    CatalogMediaVariantKindContract Kind,
    string ObjectKey,
    string ContentType,
    string ContentDigest,
    long Size,
    int Width,
    int Height,
    DateTimeOffset CreatedAtUtc);

public sealed record CatalogMediaResponse(
    Guid Id,
    string CatalogKey,
    CatalogMediaStateContract State,
    string QuarantineObjectKey,
    string ExpectedContentType,
    string ExpectedContentDigest,
    long ExpectedSize,
    CatalogMediaRightsBasisContract RightsBasis,
    string RightsReference,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset ChangedAtUtc,
    long AggregateRevision,
    DateTimeOffset? UploadAuthorizationExpiresAtUtc,
    DateTimeOffset? UploadedAtUtc,
    DateTimeOffset? ScannedAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? RightsRevokedAtUtc,
    string? FailureCode,
    IReadOnlyList<CatalogMediaVariantResponse> Variants);

public sealed record CatalogMediaUploadAuthorizationResponse(
    CatalogMediaResponse Asset,
    Uri UploadUri,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyDictionary<string, string> RequiredHeaders);

public static class CatalogMediaIntegrationEventTypes
{
    public const string Accepted = "catalog.media.accepted";
    public const string RightsRevoked = "catalog.media.rights-revoked";
}

public static class CatalogMediaIntegrationEventContracts
{
    public const string Accepted = "aggregator.catalog-media.accepted@1";
    public const string RightsRevoked = "aggregator.catalog-media.rights-revoked@1";
}

public sealed record CatalogMediaAccepted(
    Guid EventId,
    Guid AssetId,
    string CatalogKey,
    long AggregateRevision,
    IReadOnlyList<CatalogMediaVariantResponse> Variants,
    DateTimeOffset OccurredAtUtc);

public sealed record CatalogMediaRightsRevoked(
    Guid EventId,
    Guid AssetId,
    string CatalogKey,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);
''')

write(
    APPLICATION / "CatalogMedia.Application.csproj",
    '''<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../CatalogMedia.Domain/CatalogMedia.Domain.csproj" />
    <ProjectReference Include="../CatalogMedia.Contracts/CatalogMedia.Contracts.csproj" />
  </ItemGroup>
</Project>''',
)
write(
    APPLICATION / "CatalogMediaApplication.cs",
    r'''
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.CatalogMedia.Contracts;
using Aggregator.CatalogMedia.Domain;

namespace Aggregator.CatalogMedia.Application;

public interface ICatalogMediaClock
{
    DateTimeOffset GetUtcNow();
}

public interface ICatalogMediaIdSource
{
    Guid CreateId();
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
    CatalogMediaAsset Asset);

public interface ICatalogMediaRepository
{
    Task<CatalogMediaCommandResult?> ReadCommandResultAsync(
        CatalogMediaCommandIdentity commandIdentity,
        CancellationToken cancellationToken);
    Task<CatalogMediaCommandResult> AddAsync(
        CatalogMediaAsset asset,
        CatalogMediaCommandIdentity commandIdentity,
        CatalogMediaCommandContext context,
        CancellationToken cancellationToken);
    Task<CatalogMediaCommandResult> SaveAsync(
        CatalogMediaAsset asset,
        long expectedStoredAggregateRevision,
        CatalogMediaCommandIdentity commandIdentity,
        CatalogMediaCommandContext context,
        CatalogMediaOutboxMessage? outbox,
        CancellationToken cancellationToken);
    Task<CatalogMediaAsset?> GetAsync(Guid assetId, CancellationToken cancellationToken);
    Task<CatalogMediaProcessingLease?> TryLeaseUploadedAsync(
        string workerIdentity,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken);
    Task CompleteProcessingAsync(
        CatalogMediaProcessingLease lease,
        CatalogMediaAsset asset,
        CatalogMediaOutboxMessage outbox,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
    Task<int> RecordProcessingFailureAsync(
        CatalogMediaProcessingLease lease,
        string error,
        bool terminal,
        int maximumAttempts,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);
}

public interface ICatalogMediaObjectStore
{
    Task<CatalogMediaUploadAuthorization> CreateUploadAuthorizationAsync(
        CatalogMediaAsset asset,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
    Task<CatalogMediaObjectDescriptor> VerifyUploadedAsync(
        CatalogMediaAsset asset,
        CancellationToken cancellationToken);
    Task<Stream> OpenQuarantineReadAsync(CatalogMediaAsset asset, CancellationToken cancellationToken);
    Task<CatalogMediaObjectDescriptor> PutVariantAsync(
        CatalogMediaAsset asset,
        CatalogMediaVariantKind kind,
        string contentType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
    Task DeleteQuarantineAsync(CatalogMediaAsset asset, CancellationToken cancellationToken);
}

public interface ICatalogMediaScanner
{
    Task<CatalogMediaScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

public interface ICatalogMediaVariantProcessor
{
    Task<IReadOnlyList<CatalogMediaVariantContent>> CreateVariantsAsync(
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
        var digest = CatalogMediaCanonicalJson.ComputeDigest(new { assetId, request });
        var command = CatalogMediaCommandIdentity.Create($"catalog-media.{assetId:N}.upload-authorize", idempotencyKey, digest);
        var replay = await repository.ReadCommandResultAsync(command, cancellationToken);
        if (replay is not null)
        {
            var replayAuthorization = await objectStore.CreateUploadAuthorizationAsync(
                replay.Asset,
                replay.Asset.UploadAuthorizationExpiresAtUtc!.Value - clock.GetUtcNow(),
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
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        var lease = await repository.TryLeaseUploadedAsync(
            workerIdentity, clock.GetUtcNow(), leaseDuration, maximumAttempts, cancellationToken);
        if (lease is null) return false;
        try
        {
            await using var scanStream = await objectStore.OpenQuarantineReadAsync(lease.Asset, cancellationToken);
            var scan = await scanner.ScanAsync(scanStream, cancellationToken);
            var asset = lease.Asset;
            var storedRevision = asset.AggregateRevision;
            var now = clock.GetUtcNow();
            if (!scan.IsClean)
            {
                asset.Reject(asset.AggregateRevision, "malware-detected", now);
                var rejectionEventId = idSource.CreateId();
                var rejection = new CatalogMediaRightsRevoked(
                    rejectionEventId, asset.Id, asset.CatalogKey, asset.AggregateRevision, now);
                var rejectionOutbox = CatalogMediaCanonicalJson.ToOutbox(
                    rejectionEventId, "catalog.media.rejected", "aggregator.catalog-media.rejected@1",
                    rejection, now, CatalogMediaCommandContext.Start(CatalogMediaActor.Create(idSource.CreateId()), workerIdentity));
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
            var context = CatalogMediaCommandContext.Start(CatalogMediaActor.Create(idSource.CreateId()), workerIdentity);
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
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    public static string ComputeDigest<T>(T value) => ComputeDigest(Serialize(value));

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
        var bytes = Serialize(payload);
        return new CatalogMediaOutboxMessage(
            eventId,
            routingKey,
            contractIdentity,
            Encoding.UTF8.GetString(bytes),
            ComputeDigest(bytes),
            occurredAtUtc,
            context.CorrelationId,
            context.CausationId);
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
''')

print("Catalog media Domain, Contracts, and Application source generated.")
