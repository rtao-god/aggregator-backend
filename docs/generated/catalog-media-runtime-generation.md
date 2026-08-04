# Catalog media runtime generation

- Object metadata key: `StoredObjectDescriptor.Key`.
- Object metadata content type: `StoredObjectDescriptor.ContentType`.
- Object metadata digest: `StoredObjectDescriptor.Sha256`.
- Object metadata size: `StoredObjectDescriptor.Size`.
- Upload response URI: `CatalogMediaUploadAuthorization.UploadUri`.
- Upload response expiry: `CatalogMediaUploadAuthorization.ExpiresAtUtc`.
- Upload response required headers: `CatalogMediaUploadAuthorization.RequiredHeaders`.
- Object-store upload method: `IObjectStore.CreateScopedWriteUrlAsync`.
- Media state, variants, commands, processing leases and outbox are persisted in `catalog_db`.
- Publication insertion is blocked unless every referenced media asset is accepted and rights-active.
