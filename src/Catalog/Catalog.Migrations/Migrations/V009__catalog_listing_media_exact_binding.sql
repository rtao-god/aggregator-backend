DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM catalog.media) THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog listing media exact-binding migration is blocked by legacy rows.',
            DETAIL = 'Existing rows contain caller-authored media metadata and do not prove an exact Catalog Media asset revision and variant.',
            HINT = 'Recreate each affected listing revision through the current Catalog command contract before applying Catalog V009.';
    END IF;
END
$$;

ALTER TABLE catalog.media
    DROP CONSTRAINT IF EXISTS fk_catalog_listing_media_asset,
    DROP CONSTRAINT IF EXISTS media_rights_basis_valid,
    DROP COLUMN IF EXISTS rights_reference,
    ADD COLUMN media_aggregate_revision bigint NOT NULL,
    ADD COLUMN variant_id uuid NOT NULL,
    ADD COLUMN display_order integer NOT NULL,
    ADD COLUMN caption varchar(500) NULL,
    ADD CONSTRAINT media_aggregate_revision_positive CHECK (media_aggregate_revision > 0),
    ADD CONSTRAINT media_variant_id_nonempty CHECK (variant_id <> '00000000-0000-0000-0000-000000000000'),
    ADD CONSTRAINT media_content_type_valid CHECK (content_type IN ('image/jpeg', 'image/png', 'image/webp')),
    ADD CONSTRAINT media_rights_basis_valid CHECK (rights_basis IN (1, 2, 4)),
    ADD CONSTRAINT media_display_order_nonnegative CHECK (display_order >= 0),
    ADD CONSTRAINT media_caption_valid CHECK
    (
        caption IS NULL
        OR
        (
            length(btrim(caption)) > 0
            AND length(caption) <= 500
            AND caption !~ '[[:cntrl:]]'
        )
    ),
    ADD CONSTRAINT media_owner_uri_exact CHECK
    (
        object_uri =
            'urn:aggregator:catalog-media:'
            || replace(media_id::text, '-', '')
            || ':'
            || replace(variant_id::text, '-', '')
            || ':'
            || content_digest
    ),
    ADD CONSTRAINT media_variant_unique UNIQUE (listing_revision_id, variant_id),
    ADD CONSTRAINT media_display_order_unique UNIQUE (listing_revision_id, display_order);

DO $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_catalog_media_variant_asset_id'
          AND conrelid = 'media.variant'::regclass
    )
    THEN
        ALTER TABLE media.variant
            ADD CONSTRAINT uq_catalog_media_variant_asset_id UNIQUE (asset_id, id);
    END IF;
END
$$;

ALTER TABLE catalog.media
    ADD CONSTRAINT fk_catalog_listing_media_variant
    FOREIGN KEY (media_id, variant_id)
    REFERENCES media.variant (asset_id, id)
    ON DELETE RESTRICT;

CREATE OR REPLACE FUNCTION catalog.ensure_listing_media_binding_exact()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM catalog.listing_revision AS revision
        JOIN catalog.listing AS listing
          ON listing.id = revision.listing_id
        JOIN media.asset AS asset
          ON asset.id = NEW.media_id
        JOIN media.variant AS variant
          ON variant.asset_id = asset.id
         AND variant.id = NEW.variant_id
        WHERE revision.id = NEW.listing_revision_id
          AND asset.catalog_key = listing.catalog_key
          AND asset.aggregate_revision = NEW.media_aggregate_revision
          AND asset.state = 5
          AND asset.accepted_at_utc IS NOT NULL
          AND asset.rights_revoked_at_utc IS NULL
          AND variant.content_type = NEW.content_type
          AND variant.content_digest = NEW.content_digest
          AND NEW.rights_basis = CASE asset.rights_basis
              WHEN 1 THEN 1
              WHEN 2 THEN 2
              WHEN 3 THEN 4
              ELSE 0
          END
          AND NEW.object_uri =
              'urn:aggregator:catalog-media:'
              || replace(NEW.media_id::text, '-', '')
              || ':'
              || replace(NEW.variant_id::text, '-', '')
              || ':'
              || NEW.content_digest
    )
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog listing revision media binding does not match the exact accepted Catalog Media owner state.',
            HINT = 'Reload the exact media asset revision and variant through Catalog Media before creating the listing revision.';
    END IF;

    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS tr_catalog_listing_media_binding_exact ON catalog.media;
CREATE TRIGGER tr_catalog_listing_media_binding_exact
    BEFORE INSERT OR UPDATE ON catalog.media
    FOR EACH ROW EXECUTE FUNCTION catalog.ensure_listing_media_binding_exact();

CREATE OR REPLACE FUNCTION catalog.ensure_publication_media_safe()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM catalog.media AS reference
        JOIN catalog.publication AS publication
          ON publication.id = NEW.publication_id
        LEFT JOIN media.asset AS asset
          ON asset.id = reference.media_id
        LEFT JOIN media.variant AS variant
          ON variant.asset_id = reference.media_id
         AND variant.id = reference.variant_id
        WHERE reference.listing_revision_id = NEW.listing_revision_id
          AND
          (
              asset.id IS NULL
              OR variant.id IS NULL
              OR asset.catalog_key <> publication.catalog_key
              OR asset.aggregate_revision <> reference.media_aggregate_revision
              OR asset.state <> 5
              OR asset.accepted_at_utc IS NULL
              OR asset.rights_revoked_at_utc IS NOT NULL
              OR variant.content_type <> reference.content_type
              OR variant.content_digest <> reference.content_digest
              OR reference.rights_basis <> CASE asset.rights_basis
                  WHEN 1 THEN 1
                  WHEN 2 THEN 2
                  WHEN 3 THEN 4
                  ELSE 0
              END
              OR reference.object_uri <>
                  'urn:aggregator:catalog-media:'
                  || replace(reference.media_id::text, '-', '')
                  || ':'
                  || replace(reference.variant_id::text, '-', '')
                  || ':'
                  || reference.content_digest
          )
    )
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog publication references media that no longer matches its exact accepted owner revision and variant.',
            HINT = 'Create a new listing revision from current Catalog Media owner output before publication.';
    END IF;

    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS tr_catalog_publication_media_safe ON catalog.publication_entry;
CREATE TRIGGER tr_catalog_publication_media_safe
    BEFORE INSERT OR UPDATE OF listing_revision_id ON catalog.publication_entry
    FOR EACH ROW EXECUTE FUNCTION catalog.ensure_publication_media_safe();
