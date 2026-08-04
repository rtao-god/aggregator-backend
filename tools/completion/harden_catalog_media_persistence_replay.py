#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INFRA = ROOT / "src" / "Catalog" / "Catalog.Media.Infrastructure"

json_path = INFRA / "CatalogMediaPersistenceJson.cs"
text = json_path.read_text(encoding="utf-8")
anchor = '''internal sealed record CatalogMediaPersistenceSnapshot(
    Guid Id,'''
wrapper = '''internal sealed record CatalogMediaCommandPersistenceResult(
    CatalogMediaPersistenceSnapshot Asset,
    CatalogMediaUploadAuthorization? UploadAuthorization);

internal sealed record CatalogMediaPersistenceSnapshot(
    Guid Id,'''
if anchor not in text:
    raise RuntimeError("Catalog media command persistence wrapper anchor is missing.")
text = text.replace(anchor, wrapper, 1)
old_methods = '''    public static byte[] Serialize(CatalogMediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return CatalogMediaCanonicalJson.Serialize(ToSnapshot(asset));
    }

    public static CatalogMediaAsset Deserialize(ReadOnlySpan<byte> document, string expectedDigest)
    {
        CatalogMediaCanonicalJson.RequireDigest(expectedDigest, nameof(expectedDigest));
        var actualDigest = CatalogMediaCanonicalJson.ComputeDigest(document);
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "CATALOG_MEDIA_COMMAND_RESULT_DIGEST_MISMATCH",
                "Persisted Catalog media command result failed digest verification.",
                "Restore the exact command result from a verified catalog_db backup.");
        }
        return Restore(CatalogMediaCanonicalJson.Deserialize<CatalogMediaPersistenceSnapshot>(document));
    }'''
new_methods = '''    public static byte[] SerializeCommandResult(
        CatalogMediaAsset asset,
        CatalogMediaUploadAuthorization? uploadAuthorization)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return CatalogMediaCanonicalJson.Serialize(new CatalogMediaCommandPersistenceResult(
            ToSnapshot(asset),
            uploadAuthorization));
    }

    public static CatalogMediaCommandResult DeserializeCommandResult(
        ReadOnlySpan<byte> document,
        string expectedDigest)
    {
        CatalogMediaCanonicalJson.RequireDigest(expectedDigest, nameof(expectedDigest));
        var actualDigest = CatalogMediaCanonicalJson.ComputeDigest(document);
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "CATALOG_MEDIA_COMMAND_RESULT_DIGEST_MISMATCH",
                "Persisted Catalog media command result failed digest verification.",
                "Restore the exact command result from a verified catalog_db backup.");
        }
        var result = CatalogMediaCanonicalJson.Deserialize<CatalogMediaCommandPersistenceResult>(document);
        return new CatalogMediaCommandResult(
            Restore(result.Asset),
            true,
            result.UploadAuthorization);
    }'''
if old_methods not in text:
    raise RuntimeError("Catalog media persistence methods anchor is missing.")
json_path.write_text(text.replace(old_methods, new_methods), encoding="utf-8")

repo_path = INFRA / "EfCatalogMediaRepository.cs"
text = repo_path.read_text(encoding="utf-8")
text = text.replace(
    '''                CatalogMediaCommandContext context,
                CatalogMediaOutboxMessage? outbox,
                CancellationToken cancellationToken) =>''',
    '''                CatalogMediaCommandContext context,
                CatalogMediaOutboxMessage? outbox,
                CatalogMediaUploadAuthorization? uploadAuthorization,
                CancellationToken cancellationToken) =>''',
    1,
)
text = text.replace(
    "await AddCommandResultAsync(asset, commandIdentity, context, innerCancellationToken);",
    "await AddCommandResultAsync(asset, commandIdentity, context, null, innerCancellationToken);",
    1,
)
second_anchor = "await AddCommandResultAsync(asset, commandIdentity, context, innerCancellationToken);"
if second_anchor not in text:
    raise RuntimeError("Catalog media save command result anchor is missing.")
text = text.replace(
    second_anchor,
    "await AddCommandResultAsync(asset, commandIdentity, context, uploadAuthorization, innerCancellationToken);",
    1,
)
text = text.replace(
    '''            private Task AddCommandResultAsync(
                CatalogMediaAsset asset,
                CatalogMediaCommandIdentity identity,
                CatalogMediaCommandContext context,
                CancellationToken cancellationToken)''',
    '''            private Task AddCommandResultAsync(
                CatalogMediaAsset asset,
                CatalogMediaCommandIdentity identity,
                CatalogMediaCommandContext context,
                CatalogMediaUploadAuthorization? uploadAuthorization,
                CancellationToken cancellationToken)''',
    1,
)
text = text.replace(
    "var document = CatalogMediaPersistenceJson.Serialize(asset);",
    "var document = CatalogMediaPersistenceJson.SerializeCommandResult(asset, uploadAuthorization);",
    1,
)
old_restore = '''                return new CatalogMediaCommandResult(
                    CatalogMediaPersistenceJson.Deserialize(row.ResultDocument, row.ResultDigest),
                    true);'''
new_restore = '''                return CatalogMediaPersistenceJson.DeserializeCommandResult(
                    row.ResultDocument,
                    row.ResultDigest);'''
if old_restore not in text:
    raise RuntimeError("Catalog media command result restore anchor is missing.")
repo_path.write_text(text.replace(old_restore, new_restore), encoding="utf-8")
