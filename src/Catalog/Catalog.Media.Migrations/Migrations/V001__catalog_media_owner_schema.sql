CREATE SCHEMA IF NOT EXISTS media;
CREATE SCHEMA IF NOT EXISTS media_messaging;
CREATE SCHEMA IF NOT EXISTS operations;

CREATE TABLE media.asset
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

CREATE INDEX ix_catalog_media_asset_catalog_state
    ON media.asset (catalog_key, state, registered_at_utc);

CREATE TABLE media.variant
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

CREATE TABLE operations.media_command_result
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

CREATE TABLE operations.processing_work
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

CREATE INDEX ix_catalog_media_processing_available
    ON operations.processing_work (lease_expires_at_utc, attempt_count)
    WHERE completed_at_utc IS NULL;

CREATE TABLE media_messaging.outbox_message
(
    message_id uuid PRIMARY KEY,
    routing_key varchar(256) NOT NULL,
    contract_identity varchar(256) NOT NULL,
    payload_json jsonb NOT NULL,
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
        NOT (dispatched_at_utc IS NOT NULL AND dead_lettered_at_utc IS NOT NULL))
);

CREATE INDEX ix_catalog_media_outbox_pending
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

CREATE TRIGGER tr_catalog_media_command_immutable
    BEFORE UPDATE OR DELETE ON operations.media_command_result
    FOR EACH ROW EXECUTE FUNCTION operations.reject_media_command_mutation();
