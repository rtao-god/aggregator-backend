CREATE SCHEMA IF NOT EXISTS media;
CREATE SCHEMA IF NOT EXISTS media_messaging;
CREATE SCHEMA IF NOT EXISTS operations;

DO $$
DECLARE
    existing_owner_table_count integer;
BEGIN
    SELECT count(*)
    INTO existing_owner_table_count
    FROM
    (
        VALUES
            (to_regclass('media.asset')),
            (to_regclass('media.variant')),
            (to_regclass('operations.media_command_result')),
            (to_regclass('operations.processing_work')),
            (to_regclass('media_messaging.outbox_message'))
    ) AS owner_table(table_identity)
    WHERE table_identity IS NOT NULL;

    IF existing_owner_table_count NOT IN (0, 5) THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog media owner merge is blocked by a partial legacy schema.',
            HINT = 'Restore the complete CatalogMedia owner schema or remove the uncommitted partial schema before applying Catalog V008.';
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS media.asset
(
    id uuid PRIMARY KEY,
    catalog_key varchar(120) NOT NULL,
    state integer NOT NULL,
    quarantine_object_key varchar(1024) NOT NULL UNIQUE,
    expected_content_type varchar(128) NOT NULL,
    expected_content_digest char(64) NOT NULL,
    expected_size bigint NOT NULL,
    rights_basis integer NOT NULL,
    rights_reference varchar(4000) NOT NULL,
    registered_at_utc timestamptz NOT NULL,
    changed_at_utc timestamptz NOT NULL,
    aggregate_revision bigint NOT NULL,
    upload_authorization_expires_at_utc timestamptz NULL,
    uploaded_at_utc timestamptz NULL,
    scanned_at_utc timestamptz NULL,
    accepted_at_utc timestamptz NULL,
    rights_revoked_at_utc timestamptz NULL,
    rights_revoked_by_actor_id uuid NULL,
    failure_code varchar(120) NULL,
    CONSTRAINT ck_catalog_media_asset_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_catalog_media_asset_state CHECK (state BETWEEN 1 AND 8),
    CONSTRAINT ck_catalog_media_asset_content_type CHECK (
        expected_content_type IN ('image/jpeg', 'image/png', 'image/webp')),
    CONSTRAINT ck_catalog_media_asset_digest CHECK (expected_content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_media_asset_size CHECK (expected_size BETWEEN 1 AND 104857600),
    CONSTRAINT ck_catalog_media_asset_rights_basis CHECK (rights_basis BETWEEN 1 AND 3),
    CONSTRAINT ck_catalog_media_asset_revision CHECK (aggregate_revision > 0),
    CONSTRAINT ck_catalog_media_asset_history CHECK (changed_at_utc >= registered_at_utc),
    CONSTRAINT ck_catalog_media_asset_revocation_shape CHECK (
        (rights_revoked_at_utc IS NULL AND rights_revoked_by_actor_id IS NULL)
        OR
        (rights_revoked_at_utc IS NOT NULL AND rights_revoked_by_actor_id IS NOT NULL)),
    CONSTRAINT ck_catalog_media_asset_accepted_shape CHECK (
        state <> 5 OR (accepted_at_utc IS NOT NULL AND rights_revoked_at_utc IS NULL)),
    CONSTRAINT ck_catalog_media_asset_revoked_shape CHECK (
        state <> 7 OR rights_revoked_at_utc IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_catalog_media_asset_catalog_state
    ON media.asset (catalog_key, state, registered_at_utc);

CREATE TABLE IF NOT EXISTS media.variant
(
    id uuid PRIMARY KEY,
    asset_id uuid NOT NULL REFERENCES media.asset (id) ON DELETE RESTRICT,
    kind integer NOT NULL,
    object_key varchar(1024) NOT NULL UNIQUE,
    content_type varchar(128) NOT NULL,
    content_digest char(64) NOT NULL,
    size bigint NOT NULL,
    width integer NOT NULL,
    height integer NOT NULL,
    created_at_utc timestamptz NOT NULL,
    CONSTRAINT uq_catalog_media_variant_kind UNIQUE (asset_id, kind),
    CONSTRAINT ck_catalog_media_variant_kind CHECK (kind BETWEEN 1 AND 4),
    CONSTRAINT ck_catalog_media_variant_content_type CHECK (
        content_type IN ('image/jpeg', 'image/png', 'image/webp')),
    CONSTRAINT ck_catalog_media_variant_digest CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_media_variant_size CHECK (size BETWEEN 1 AND 104857600),
    CONSTRAINT ck_catalog_media_variant_dimensions CHECK (
        width BETWEEN 1 AND 20000 AND height BETWEEN 1 AND 20000)
);

CREATE TABLE IF NOT EXISTS operations.media_command_result
(
    scope varchar(180) NOT NULL,
    idempotency_key varchar(200) NOT NULL,
    request_digest char(64) NOT NULL,
    asset_id uuid NOT NULL REFERENCES media.asset (id) ON DELETE RESTRICT,
    result_document bytea NOT NULL,
    result_digest char(64) NOT NULL,
    actor_id uuid NOT NULL,
    correlation_id varchar(128) NOT NULL,
    created_at_utc timestamptz NOT NULL,
    PRIMARY KEY (scope, idempotency_key),
    CONSTRAINT ck_catalog_media_command_request_digest CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_media_command_result_digest CHECK (result_digest ~ '^[0-9a-f]{64}$')
);

CREATE TABLE IF NOT EXISTS operations.processing_work
(
    asset_id uuid PRIMARY KEY REFERENCES media.asset (id) ON DELETE RESTRICT,
    lease_token uuid NULL,
    leased_by varchar(200) NULL,
    lease_expires_at_utc timestamptz NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    last_error varchar(4000) NULL,
    last_failed_at_utc timestamptz NULL,
    completed_at_utc timestamptz NULL,
    CONSTRAINT ck_catalog_media_processing_attempts CHECK (attempt_count >= 0),
    CONSTRAINT ck_catalog_media_processing_lease_shape CHECK (
        (lease_token IS NULL AND leased_by IS NULL AND lease_expires_at_utc IS NULL)
        OR
        (lease_token IS NOT NULL AND leased_by IS NOT NULL AND lease_expires_at_utc IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS ix_catalog_media_processing_available
    ON operations.processing_work (lease_expires_at_utc, attempt_count)
    WHERE completed_at_utc IS NULL;

CREATE TABLE IF NOT EXISTS media_messaging.outbox_message
(
    message_id uuid PRIMARY KEY,
    routing_key varchar(256) NOT NULL,
    contract_identity varchar(256) NOT NULL,
    payload_json text NOT NULL,
    payload_digest char(64) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    correlation_id varchar(128) NOT NULL,
    causation_id uuid NULL,
    lease_token uuid NULL,
    leased_by varchar(200) NULL,
    lease_expires_at_utc timestamptz NULL,
    delivery_attempts integer NOT NULL DEFAULT 0,
    dispatched_at_utc timestamptz NULL,
    last_error varchar(2000) NULL,
    dead_lettered_at_utc timestamptz NULL,
    dead_letter_reason varchar(2000) NULL,
    CONSTRAINT ck_catalog_media_outbox_digest CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_media_outbox_attempts CHECK (delivery_attempts >= 0),
    CONSTRAINT ck_catalog_media_outbox_lease_shape CHECK (
        (lease_token IS NULL AND leased_by IS NULL AND lease_expires_at_utc IS NULL)
        OR
        (lease_token IS NOT NULL AND leased_by IS NOT NULL AND lease_expires_at_utc IS NOT NULL)),
    CONSTRAINT ck_catalog_media_outbox_terminal_state CHECK (
        NOT (dispatched_at_utc IS NOT NULL AND dead_lettered_at_utc IS NOT NULL)),
    CONSTRAINT ck_catalog_media_outbox_dead_letter_shape CHECK (
        (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NULL)
        OR
        (
            dead_lettered_at_utc IS NOT NULL
            AND dead_letter_reason IS NOT NULL
            AND length(btrim(dead_letter_reason)) > 0
        ))
);

DO $$
DECLARE
    payload_data_type text;
BEGIN
    SELECT data_type
    INTO payload_data_type
    FROM information_schema.columns
    WHERE table_schema = 'media_messaging'
      AND table_name = 'outbox_message'
      AND column_name = 'payload_json';

    IF payload_data_type = 'jsonb' THEN
        IF EXISTS (SELECT 1 FROM media_messaging.outbox_message) THEN
            RAISE EXCEPTION USING
                MESSAGE = 'Catalog media owner merge cannot prove the original UTF-8 bytes of existing jsonb outbox payloads.',
                HINT = 'Drain or re-materialize every Catalog media outbox message through its producer before applying Catalog V008.';
        END IF;

        ALTER TABLE media_messaging.outbox_message
            ALTER COLUMN payload_json TYPE text
            USING payload_json::text;
    ELSIF payload_data_type IS DISTINCT FROM 'text' THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog media outbox payload storage has an unsupported data type.',
            DETAIL = format('Expected text or an empty legacy jsonb column, actual %s.', coalesce(payload_data_type, '<missing>')),
            HINT = 'Restore the exact Catalog media outbox schema before applying Catalog V008.';
    END IF;
END
$$;

DO $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM
        (
            VALUES
                ('media', 'asset', 'id'),
                ('media', 'asset', 'catalog_key'),
                ('media', 'asset', 'state'),
                ('media', 'asset', 'quarantine_object_key'),
                ('media', 'asset', 'expected_content_type'),
                ('media', 'asset', 'expected_content_digest'),
                ('media', 'asset', 'expected_size'),
                ('media', 'asset', 'rights_basis'),
                ('media', 'asset', 'rights_reference'),
                ('media', 'asset', 'registered_at_utc'),
                ('media', 'asset', 'changed_at_utc'),
                ('media', 'asset', 'aggregate_revision'),
                ('media', 'variant', 'asset_id'),
                ('media', 'variant', 'kind'),
                ('media', 'variant', 'object_key'),
                ('operations', 'media_command_result', 'result_document'),
                ('operations', 'processing_work', 'lease_token'),
                ('media_messaging', 'outbox_message', 'payload_json'),
                ('media_messaging', 'outbox_message', 'dead_lettered_at_utc'),
                ('media_messaging', 'outbox_message', 'dead_letter_reason')
        ) AS expected(schema_name, table_name, column_name)
        LEFT JOIN information_schema.columns AS actual
          ON actual.table_schema = expected.schema_name
         AND actual.table_name = expected.table_name
         AND actual.column_name = expected.column_name
        WHERE actual.column_name IS NULL
    )
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog media owner merge found an incompatible legacy table shape.',
            HINT = 'Restore the exact CatalogMedia migration state before transferring ownership to Catalog.';
    END IF;
END
$$;

DO $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM media_messaging.outbox_message
        WHERE
            (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NOT NULL)
            OR
            (
                dead_lettered_at_utc IS NOT NULL
                AND
                (
                    dead_letter_reason IS NULL
                    OR length(btrim(dead_letter_reason)) = 0
                )
            )
    )
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog media owner merge is blocked by an incomplete outbox dead-letter state.',
            HINT = 'Repair each row so dead-letter timestamp and non-empty reason are either both present or both absent.';
    END IF;

    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_catalog_media_outbox_dead_letter_shape'
          AND conrelid = 'media_messaging.outbox_message'::regclass
    )
    THEN
        ALTER TABLE media_messaging.outbox_message
            ADD CONSTRAINT ck_catalog_media_outbox_dead_letter_shape CHECK
            (
                (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NULL)
                OR
                (
                    dead_lettered_at_utc IS NOT NULL
                    AND dead_letter_reason IS NOT NULL
                    AND length(btrim(dead_letter_reason)) > 0
                )
            );
    END IF;
END
$$;

CREATE INDEX IF NOT EXISTS ix_catalog_media_outbox_pending
    ON media_messaging.outbox_message (occurred_at_utc, message_id)
    WHERE dispatched_at_utc IS NULL AND dead_lettered_at_utc IS NULL;

CREATE OR REPLACE FUNCTION media.reject_immutable_variant_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Catalog media variants are immutable';
END
$$;

DROP TRIGGER IF EXISTS tr_catalog_media_variant_immutable ON media.variant;
CREATE TRIGGER tr_catalog_media_variant_immutable
    BEFORE UPDATE OR DELETE ON media.variant
    FOR EACH ROW EXECUTE FUNCTION media.reject_immutable_variant_mutation();

CREATE OR REPLACE FUNCTION operations.reject_media_command_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Catalog media command results are immutable';
END
$$;

DROP TRIGGER IF EXISTS tr_catalog_media_command_immutable ON operations.media_command_result;
CREATE TRIGGER tr_catalog_media_command_immutable
    BEFORE UPDATE OR DELETE ON operations.media_command_result
    FOR EACH ROW EXECUTE FUNCTION operations.reject_media_command_mutation();

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
            MESSAGE = 'Catalog media owner merge is blocked by orphan media references.',
            HINT = 'Register and verify every exact media asset before applying the publication gate.';
    END IF;

    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_catalog_listing_media_asset'
          AND conrelid = 'catalog.media'::regclass
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
