ALTER TABLE projection.catalog_visibility_block
    ADD COLUMN block_kind text NOT NULL DEFAULT 'visibility_event';

ALTER TABLE projection.catalog_visibility_block
    ALTER COLUMN suppression_id DROP NOT NULL;

ALTER TABLE projection.catalog_visibility_block
    ALTER COLUMN suppression_revision DROP NOT NULL;

ALTER TABLE projection.catalog_visibility_block
    ADD CONSTRAINT catalog_visibility_block_kind_valid
        CHECK (block_kind IN ('visibility_event', 'publication_recomposition'));

ALTER TABLE projection.catalog_visibility_block
    ADD CONSTRAINT catalog_visibility_block_owner_shape
        CHECK
        (
            (block_kind = 'visibility_event'
                AND suppression_id IS NOT NULL
                AND suppression_revision IN (2, 3))
            OR
            (block_kind = 'publication_recomposition'
                AND suppression_id IS NULL
                AND suppression_revision IS NULL)
        );

CREATE TABLE projection.publication_overlay_recomposition
(
    source_event_id uuid PRIMARY KEY,
    catalog_key text NOT NULL,
    payload_digest char(64) NOT NULL
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    previous_public_read_revision_id uuid NOT NULL
        REFERENCES projection.public_read_revision(id),
    previous_pointer_activation_revision bigint NOT NULL
        CHECK (previous_pointer_activation_revision > 0),
    promotion_overlay_id uuid NOT NULL
        REFERENCES projection.overlay_revision(id),
    safety_overlay_id uuid NOT NULL
        REFERENCES projection.overlay_revision(id),
    created_at_utc timestamptz NOT NULL
);

CREATE UNIQUE INDEX publication_overlay_recomposition_catalog_unique
    ON projection.publication_overlay_recomposition(catalog_key);
