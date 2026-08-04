DO $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM catalog.media AS reference
        LEFT JOIN media.asset AS asset ON asset.id = reference.media_id
        WHERE asset.id IS NULL
    )
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog media migration is blocked by orphan media references.',
            HINT = 'Register and verify every exact media asset before applying the publication gate.';
    END IF;
END
$$;

DO $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_catalog_listing_media_asset'
    )
    THEN
        ALTER TABLE catalog.media
            ADD CONSTRAINT fk_catalog_listing_media_asset
            FOREIGN KEY (media_id)
            REFERENCES media.asset (id)
            ON DELETE RESTRICT;
    END IF;
END
$$;

CREATE OR REPLACE FUNCTION catalog.ensure_publication_media_safe()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM catalog.media AS reference
        LEFT JOIN media.asset AS asset ON asset.id = reference.media_id
        WHERE reference.listing_revision_id = NEW.listing_revision_id
          AND
          (
              asset.id IS NULL
              OR asset.state <> 5
              OR asset.accepted_at_utc IS NULL
              OR asset.rights_revoked_at_utc IS NOT NULL
          )
    )
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog publication references media that is not accepted and rights-active.',
            HINT = 'Remove the media reference or complete scanning, variants and rights verification before publication.';
    END IF;
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS tr_catalog_publication_media_safe ON catalog.publication_entry;
CREATE TRIGGER tr_catalog_publication_media_safe
    BEFORE INSERT OR UPDATE OF listing_revision_id ON catalog.publication_entry
    FOR EACH ROW EXECUTE FUNCTION catalog.ensure_publication_media_safe();
