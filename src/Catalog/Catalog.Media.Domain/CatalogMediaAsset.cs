
using System.Collections.ObjectModel;

namespace Aggregator.Catalog.Media.Domain;

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
