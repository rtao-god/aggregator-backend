CREATE OR REPLACE FUNCTION catalog.assert_publication_activation_safe
(
    p_catalog_key varchar,
    p_publication_id uuid,
    p_publication_sequence bigint,
    p_activated_at_utc timestamptz
)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM catalog.publication AS publication
        WHERE publication.id = p_publication_id
          AND publication.catalog_key = p_catalog_key
          AND publication.sequence = p_publication_sequence
    )
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7101',
            MESSAGE = 'Catalog publication activation target does not match its pointer identity.',
            DETAIL = format(
                'Catalog %s cannot activate publication %s at sequence %s.',
                p_catalog_key,
                p_publication_id,
                p_publication_sequence),
            HINT = 'Reload the exact Catalog publication and retry with its current catalog and sequence identities.';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM catalog.publication_entry AS entry
        JOIN catalog.media AS reference
          ON reference.listing_revision_id = entry.listing_revision_id
        LEFT JOIN media.asset AS asset
          ON asset.id = reference.media_id
        LEFT JOIN media.variant AS variant
          ON variant.asset_id = reference.media_id
         AND variant.id = reference.variant_id
        WHERE entry.publication_id = p_publication_id
          AND
          (
              asset.id IS NULL
              OR variant.id IS NULL
              OR asset.catalog_key <> p_catalog_key
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
            ERRCODE = 'P7102',
            MESSAGE = 'Catalog publication activation references media that is no longer publishable.',
            DETAIL = format(
                'Publication %s contains a media binding that does not match current accepted rights-active Catalog Media state.',
                p_publication_id),
            HINT = 'Create and approve a new listing revision from current Catalog Media owner output.';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM catalog.public_visibility_suppression AS suppression
        WHERE suppression.catalog_key = p_catalog_key
          AND suppression.state = 2
          AND suppression.starts_at_utc <= p_activated_at_utc
          AND
          (
              suppression.expires_at_utc IS NULL
              OR p_activated_at_utc < suppression.expires_at_utc
          )
          AND
          (
              suppression.target_kind IN (4, 5)
              OR
              (
                  suppression.target_kind = 1
                  AND EXISTS
                  (
                      SELECT 1
                      FROM catalog.publication_entry AS entry
                      WHERE entry.publication_id = p_publication_id
                        AND entry.listing_id = suppression.listing_id
                  )
              )
              OR
              (
                  suppression.target_kind = 2
                  AND EXISTS
                  (
                      SELECT 1
                      FROM catalog.publication_entry AS entry
                      JOIN catalog.media AS reference
                        ON reference.listing_revision_id = entry.listing_revision_id
                      WHERE entry.publication_id = p_publication_id
                        AND reference.media_id::text = suppression.target_key
                  )
              )
              OR
              (
                  suppression.target_kind = 3
                  AND EXISTS
                  (
                      SELECT 1
                      FROM catalog.publication_entry AS entry
                      JOIN catalog.contact AS contact
                        ON contact.listing_revision_id = entry.listing_revision_id
                      WHERE entry.publication_id = p_publication_id
                        AND contact.id::text = suppression.target_key
                  )
              )
          )
    )
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7103',
            MESSAGE = 'Catalog publication activation is blocked by an active public visibility suppression.',
            DETAIL = format(
                'Publication %s contains, or cannot prove absence of, an actively suppressed public target.',
                p_publication_id),
            HINT = 'Create a replacement publication without the suppressed target or resolve the suppression through its Catalog owner workflow.';
    END IF;
END
$$;

CREATE OR REPLACE FUNCTION catalog.ensure_publication_activation_safe()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM catalog.assert_publication_activation_safe
    (
        NEW.catalog_key,
        NEW.publication_id,
        NEW.publication_sequence,
        NEW.activated_at_utc
    );
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS tr_catalog_publication_activation_safe
    ON catalog.current_publication;
CREATE TRIGGER tr_catalog_publication_activation_safe
    BEFORE INSERT OR UPDATE OF
        publication_id,
        publication_sequence,
        activated_at_utc
    ON catalog.current_publication
    FOR EACH ROW EXECUTE FUNCTION catalog.ensure_publication_activation_safe();

DO $$
DECLARE
    current_pointer record;
BEGIN
    FOR current_pointer IN
        SELECT
            catalog_key,
            publication_id,
            publication_sequence,
            activated_at_utc
        FROM catalog.current_publication
    LOOP
        PERFORM catalog.assert_publication_activation_safe
        (
            current_pointer.catalog_key,
            current_pointer.publication_id,
            current_pointer.publication_sequence,
            current_pointer.activated_at_utc
        );
    END LOOP;
END
$$;
