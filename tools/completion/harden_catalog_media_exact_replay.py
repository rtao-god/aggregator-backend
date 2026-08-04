#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
path = ROOT / "src" / "Catalog" / "Catalog.Media.Application" / "CatalogMediaApplication.cs"
text = path.read_text(encoding="utf-8")

replacements = [
    (
        '''public sealed record CatalogMediaCommandResult(CatalogMediaAsset Asset, bool Replayed);''',
        '''public sealed record CatalogMediaCommandResult(
    CatalogMediaAsset Asset,
    bool Replayed,
    CatalogMediaUploadAuthorization? UploadAuthorization = null);''',
        "command result authorization",
    ),
    (
        '''        CatalogMediaCommandContext context,
        CatalogMediaOutboxMessage? outbox,
        CancellationToken cancellationToken);''',
        '''        CatalogMediaCommandContext context,
        CatalogMediaOutboxMessage? outbox,
        CatalogMediaUploadAuthorization? uploadAuthorization,
        CancellationToken cancellationToken);''',
        "repository save authorization",
    ),
    (
        '''            var expiresAtUtc = replay.Asset.UploadAuthorizationExpiresAtUtc
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
                replayAuthorization.ExpiresAtUtc, replayAuthorization.RequiredHeaders), true);''',
        '''            var replayAuthorization = replay.UploadAuthorization
                ?? throw Failure(
                    "CatalogMedia.Commands",
                    "CATALOG_MEDIA_UPLOAD_REPLAY_CORRUPT",
                    500,
                    "Persisted upload authorization command has no exact response document.",
                    "Restore the exact media command result from a verified backup.");
            if (replayAuthorization.ExpiresAtUtc <= clock.GetUtcNow())
            {
                throw Failure(
                    "CatalogMedia.Commands",
                    "CATALOG_MEDIA_UPLOAD_AUTHORIZATION_EXPIRED_REPLAY",
                    409,
                    "The replayed upload authorization has expired.",
                    "Submit a new upload-authorization command with a new Idempotency-Key.");
            }
            return (new CatalogMediaUploadAuthorizationResponse(
                CatalogMediaMapper.ToResponse(replay.Asset),
                replayAuthorization.UploadUri,
                replayAuthorization.ExpiresAtUtc,
                replayAuthorization.RequiredHeaders), true);''',
        "exact upload replay",
    ),
]
for old, new, label in replacements:
    if old not in text:
        raise RuntimeError(f"Catalog media exact replay anchor is missing: {label}")
    text = text.replace(old, new)

prepare_start = text.index("public async Task<(CatalogMediaUploadAuthorizationResponse Response, bool Replayed)> PrepareUploadAsync")
complete_start = text.index("public async Task<(CatalogMediaResponse Response, bool Replayed)> CompleteUploadAsync")
prepare = text[prepare_start:complete_start]
old_prepare_save = "repository.SaveAsync(asset, storedRevision, command, context, null, cancellationToken)"
if old_prepare_save not in prepare:
    raise RuntimeError("Prepare upload repository save anchor is missing.")
prepare = prepare.replace(
    old_prepare_save,
    "repository.SaveAsync(asset, storedRevision, command, context, null, authorization, cancellationToken)",
    1,
)
text = text[:prepare_start] + prepare + text[complete_start:]

complete_start = text.index("public async Task<(CatalogMediaResponse Response, bool Replayed)> CompleteUploadAsync")
revoke_start = text.index("public async Task<(CatalogMediaResponse Response, bool Replayed)> RevokeRightsAsync")
complete = text[complete_start:revoke_start]
old_complete_save = "repository.SaveAsync(asset, storedRevision, command, context, null, cancellationToken)"
if old_complete_save not in complete:
    raise RuntimeError("Complete upload repository save anchor is missing.")
complete = complete.replace(
    old_complete_save,
    "repository.SaveAsync(asset, storedRevision, command, context, null, null, cancellationToken)",
    1,
)
text = text[:complete_start] + complete + text[revoke_start:]

old_revoke_save = "repository.SaveAsync(asset, storedRevision, command, context, outbox, cancellationToken)"
if old_revoke_save not in text:
    raise RuntimeError("Rights revocation repository save anchor is missing.")
text = text.replace(
    old_revoke_save,
    "repository.SaveAsync(asset, storedRevision, command, context, outbox, null, cancellationToken)",
    1,
)

path.write_text(text, encoding="utf-8")
