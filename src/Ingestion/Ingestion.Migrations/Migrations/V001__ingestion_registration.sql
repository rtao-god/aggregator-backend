CREATE SCHEMA IF NOT EXISTS contracts;
CREATE SCHEMA IF NOT EXISTS catalog_projection;
CREATE SCHEMA IF NOT EXISTS batches;
CREATE SCHEMA IF NOT EXISTS operations;

CREATE TABLE contracts.producer_registration
(
    identity text PRIMARY KEY,
    active boolean NOT NULL,
    supported_contract_revisions integer[] NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT ck_producer_registration_identity
        CHECK (length(identity) BETWEEN 1 AND 200),
    CONSTRAINT ck_producer_registration_contract_revisions
        CHECK (
            cardinality(supported_contract_revisions) > 0
            AND 0 < ALL (supported_contract_revisions))
);

CREATE TABLE catalog_projection.catalog_reference
(
    site_key text NOT NULL,
    catalog_key text NOT NULL,
    active_configuration_revision_id uuid NOT NULL,
    supported_listing_kinds integer[] NOT NULL,
    aggregate_revision bigint NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_catalog_reference PRIMARY KEY (site_key, catalog_key),
    CONSTRAINT ck_catalog_reference_site_key
        CHECK (site_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_catalog_reference_catalog_key
        CHECK (catalog_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_catalog_reference_configuration_id
        CHECK (active_configuration_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_reference_listing_kinds
        CHECK (
            cardinality(supported_listing_kinds) > 0
            AND supported_listing_kinds <@ ARRAY[2, 3]::integer[]),
    CONSTRAINT ck_catalog_reference_revision_positive
        CHECK (aggregate_revision > 0)
);

CREATE TABLE batches.import_batch
(
    id uuid PRIMARY KEY,
    producer_identity text NOT NULL,
    producer_build text NOT NULL,
    collector_export_id uuid NOT NULL,
    collector_export_digest character(64) NOT NULL,
    target_site_key text NOT NULL,
    target_catalog_key text NOT NULL,
    target_catalog_configuration_revision_id uuid NOT NULL,
    expected_item_count integer NOT NULL,
    manifest_digest character(64) NOT NULL,
    item_index_digest character(64) NOT NULL,
    payload_digest character(64) NOT NULL,
    payload_object_key text NOT NULL,
    payload_object_digest character(64) NOT NULL,
    payload_object_size bigint NOT NULL,
    payload_content_type text NOT NULL,
    registered_at_utc timestamp with time zone NOT NULL,
    last_changed_at_utc timestamp with time zone NOT NULL,
    state integer NOT NULL,
    aggregate_revision bigint NOT NULL,
    accepted_item_count integer NOT NULL DEFAULT 0,
    review_required_item_count integer NOT NULL DEFAULT 0,
    rejected_item_count integer NOT NULL DEFAULT 0,
    failure_code text NULL,
    CONSTRAINT uq_import_batch_producer_export UNIQUE (producer_identity, collector_export_id),
    CONSTRAINT ck_import_batch_id
        CHECK (id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_import_batch_producer_identity
        CHECK (length(producer_identity) BETWEEN 1 AND 200),
    CONSTRAINT ck_import_batch_producer_build
        CHECK (length(producer_build) BETWEEN 1 AND 200),
    CONSTRAINT ck_import_batch_export_id
        CHECK (collector_export_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_import_batch_export_digest
        CHECK (collector_export_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batch_site_key
        CHECK (target_site_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_import_batch_catalog_key
        CHECK (target_catalog_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_import_batch_configuration_id
        CHECK (target_catalog_configuration_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_import_batch_item_count
        CHECK (expected_item_count BETWEEN 1 AND 100000),
    CONSTRAINT ck_import_batch_manifest_digest
        CHECK (manifest_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batch_item_index_digest
        CHECK (item_index_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batch_payload_digest
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batch_payload_key
        CHECK (
            length(payload_object_key) BETWEEN 1 AND 1024
            AND payload_object_key !~ '(^/|\\|\.\.)'),
    CONSTRAINT ck_import_batch_payload_object_digest
        CHECK (payload_object_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batch_payload_size
        CHECK (payload_object_size > 0),
    CONSTRAINT ck_import_batch_payload_content_type
        CHECK (length(payload_content_type) BETWEEN 1 AND 200),
    CONSTRAINT ck_import_batch_state
        CHECK (state BETWEEN 1 AND 18),
    CONSTRAINT ck_import_batch_revision
        CHECK (aggregate_revision > 0),
    CONSTRAINT ck_import_batch_decision_counts
        CHECK (
            accepted_item_count >= 0
            AND review_required_item_count >= 0
            AND rejected_item_count >= 0
            AND accepted_item_count + review_required_item_count + rejected_item_count <= expected_item_count),
    CONSTRAINT ck_import_batch_time_order
        CHECK (last_changed_at_utc >= registered_at_utc),
    CONSTRAINT ck_import_batch_failure_code
        CHECK (failure_code IS NULL OR length(failure_code) BETWEEN 1 AND 200)
);

CREATE INDEX ix_import_batch_catalog_state_registered
    ON batches.import_batch (target_catalog_key, state, registered_at_utc);

CREATE TABLE batches.import_batch_manifest
(
    batch_id uuid PRIMARY KEY,
    contract_identity text NOT NULL,
    contract_revision integer NOT NULL,
    canonical_document bytea NOT NULL,
    content_digest character(64) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT fk_import_batch_manifest_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_import_batch_manifest_contract
        CHECK (length(contract_identity) BETWEEN 1 AND 200),
    CONSTRAINT ck_import_batch_manifest_revision
        CHECK (contract_revision > 0),
    CONSTRAINT ck_import_batch_manifest_document
        CHECK (octet_length(canonical_document) > 0),
    CONSTRAINT ck_import_batch_manifest_digest
        CHECK (content_digest ~ '^[0-9a-f]{64}$')
);

CREATE TABLE batches.import_batch_source_policy
(
    batch_id uuid NOT NULL,
    source_key text NOT NULL,
    policy_digest character(64) NOT NULL,
    usage_policy integer NOT NULL,
    CONSTRAINT pk_import_batch_source_policy PRIMARY KEY (batch_id, source_key),
    CONSTRAINT fk_import_batch_source_policy_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_import_batch_source_policy_key
        CHECK (source_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_import_batch_source_policy_digest
        CHECK (policy_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batch_source_policy_usage
        CHECK (usage_policy BETWEEN 1 AND 7)
);

CREATE TABLE batches.import_batch_artifact
(
    batch_id uuid NOT NULL,
    role integer NOT NULL,
    object_key text NOT NULL,
    content_digest character(64) NOT NULL,
    size bigint NOT NULL,
    content_type text NOT NULL,
    CONSTRAINT pk_import_batch_artifact PRIMARY KEY (batch_id, role, object_key),
    CONSTRAINT uq_import_batch_artifact_object_key UNIQUE (object_key),
    CONSTRAINT fk_import_batch_artifact_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_import_batch_artifact_role
        CHECK (role BETWEEN 1 AND 2),
    CONSTRAINT ck_import_batch_artifact_object_key
        CHECK (
            length(object_key) BETWEEN 1 AND 1024
            AND object_key !~ '(^/|\\|\.\.)'),
    CONSTRAINT ck_import_batch_artifact_digest
        CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batch_artifact_size
        CHECK (size > 0),
    CONSTRAINT ck_import_batch_artifact_content_type
        CHECK (length(content_type) BETWEEN 1 AND 200)
);

CREATE TABLE operations.command_idempotency
(
    scope text NOT NULL,
    key text NOT NULL,
    request_digest character(64) NOT NULL,
    batch_id uuid NOT NULL,
    caller_service_identity text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_command_idempotency PRIMARY KEY (scope, key),
    CONSTRAINT fk_command_idempotency_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_command_idempotency_scope
        CHECK (length(scope) BETWEEN 1 AND 150),
    CONSTRAINT ck_command_idempotency_key
        CHECK (length(key) BETWEEN 1 AND 200),
    CONSTRAINT ck_command_idempotency_digest
        CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_command_idempotency_caller
        CHECK (length(caller_service_identity) BETWEEN 1 AND 200)
);

CREATE OR REPLACE FUNCTION operations.reject_immutable_ingestion_record()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'immutable Ingestion record % may not be updated or deleted', TG_TABLE_NAME
        USING ERRCODE = '55000';
END;
$$;

CREATE TRIGGER trg_import_batch_manifest_immutable
    BEFORE UPDATE OR DELETE ON batches.import_batch_manifest
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();

CREATE TRIGGER trg_import_batch_source_policy_immutable
    BEFORE UPDATE OR DELETE ON batches.import_batch_source_policy
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();

CREATE TRIGGER trg_import_batch_artifact_immutable
    BEFORE UPDATE OR DELETE ON batches.import_batch_artifact
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();

CREATE TRIGGER trg_command_idempotency_immutable
    BEFORE UPDATE OR DELETE ON operations.command_idempotency
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();
