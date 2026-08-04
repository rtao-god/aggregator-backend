CREATE SCHEMA IF NOT EXISTS processing;
CREATE SCHEMA IF NOT EXISTS processing_operations;

CREATE TABLE processing.validation_job
(
    batch_id uuid PRIMARY KEY,
    state integer NOT NULL,
    worker_identity text NULL,
    lease_expires_at_utc timestamp with time zone NULL,
    attempt_count integer NOT NULL,
    payload_digest character(64) NULL,
    failure_code text NULL,
    created_at_utc timestamp with time zone NOT NULL,
    last_changed_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT fk_ingestion_validation_job_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_ingestion_validation_job_state CHECK (state BETWEEN 1 AND 4),
    CONSTRAINT ck_ingestion_validation_job_attempt CHECK (attempt_count > 0),
    CONSTRAINT ck_ingestion_validation_job_digest CHECK (payload_digest IS NULL OR payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_ingestion_validation_job_lease CHECK
        ((state = 2 AND worker_identity IS NOT NULL AND lease_expires_at_utc IS NOT NULL)
         OR (state <> 2)),
    CONSTRAINT ck_ingestion_validation_job_time CHECK (last_changed_at_utc >= created_at_utc)
);

CREATE INDEX ix_ingestion_validation_job_lease
    ON processing.validation_job (state, lease_expires_at_utc);

CREATE TABLE processing.item_decision
(
    decision_id uuid PRIMARY KEY,
    batch_id uuid NOT NULL,
    item_key text NOT NULL,
    item_digest character(64) NOT NULL,
    decision integer NOT NULL,
    reason_codes text[] NOT NULL,
    supersedes_decision_id uuid NULL,
    decided_at_utc timestamp with time zone NOT NULL,
    decided_by text NOT NULL,
    item_document bytea NOT NULL,
    item_document_digest character(64) NOT NULL,
    CONSTRAINT fk_ingestion_item_decision_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_ingestion_item_decision_supersedes
        FOREIGN KEY (supersedes_decision_id)
        REFERENCES processing.item_decision (decision_id)
        ON DELETE RESTRICT,
    CONSTRAINT uq_ingestion_item_decision_supersedes UNIQUE (supersedes_decision_id),
    CONSTRAINT ck_ingestion_item_decision_id CHECK (decision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_ingestion_item_decision_key CHECK (length(item_key) BETWEEN 1 AND 200),
    CONSTRAINT ck_ingestion_item_decision_digest CHECK (item_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_ingestion_item_decision_state CHECK (decision BETWEEN 1 AND 3),
    CONSTRAINT ck_ingestion_item_decision_reasons CHECK
        ((decision = 1 AND cardinality(reason_codes) >= 0)
         OR (decision IN (2, 3) AND cardinality(reason_codes) > 0)),
    CONSTRAINT ck_ingestion_item_decision_actor CHECK (length(decided_by) BETWEEN 1 AND 200),
    CONSTRAINT ck_ingestion_item_decision_document CHECK (octet_length(item_document) > 0),
    CONSTRAINT ck_ingestion_item_decision_document_digest CHECK (item_document_digest ~ '^[0-9a-f]{64}$')
);

CREATE INDEX ix_ingestion_item_decision_current
    ON processing.item_decision (batch_id, item_key, decided_at_utc DESC, decision_id DESC);

CREATE TABLE processing.catalog_delivery
(
    delivery_id uuid PRIMARY KEY,
    batch_id uuid NOT NULL,
    item_key text NOT NULL,
    command_type text NOT NULL,
    command_document bytea NOT NULL,
    command_digest character(64) NOT NULL,
    state integer NOT NULL,
    attempt_count integer NOT NULL,
    worker_identity text NULL,
    lease_expires_at_utc timestamp with time zone NULL,
    catalog_listing_id uuid NULL,
    catalog_listing_revision_id uuid NULL,
    failure_code text NULL,
    created_at_utc timestamp with time zone NOT NULL,
    last_changed_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT fk_ingestion_catalog_delivery_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT uq_ingestion_catalog_delivery_item UNIQUE (batch_id, item_key),
    CONSTRAINT ck_ingestion_catalog_delivery_id CHECK (delivery_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_ingestion_catalog_delivery_item CHECK (length(item_key) BETWEEN 1 AND 200),
    CONSTRAINT ck_ingestion_catalog_delivery_command CHECK (length(command_type) BETWEEN 1 AND 200),
    CONSTRAINT ck_ingestion_catalog_delivery_document CHECK (octet_length(command_document) > 0),
    CONSTRAINT ck_ingestion_catalog_delivery_digest CHECK (command_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_ingestion_catalog_delivery_state CHECK (state BETWEEN 1 AND 4),
    CONSTRAINT ck_ingestion_catalog_delivery_attempt CHECK (attempt_count >= 0),
    CONSTRAINT ck_ingestion_catalog_delivery_lease CHECK
        ((state = 2 AND worker_identity IS NOT NULL AND lease_expires_at_utc IS NOT NULL)
         OR (state <> 2)),
    CONSTRAINT ck_ingestion_catalog_delivery_outcome CHECK
        ((state = 3 AND catalog_listing_id IS NOT NULL AND catalog_listing_revision_id IS NOT NULL AND failure_code IS NULL)
         OR (state = 4 AND failure_code IS NOT NULL)
         OR (state IN (1, 2) AND catalog_listing_id IS NULL AND catalog_listing_revision_id IS NULL AND failure_code IS NULL)),
    CONSTRAINT ck_ingestion_catalog_delivery_time CHECK (last_changed_at_utc >= created_at_utc)
);

CREATE INDEX ix_ingestion_catalog_delivery_lease
    ON processing.catalog_delivery (state, lease_expires_at_utc, created_at_utc);

CREATE TABLE processing_operations.command_result
(
    scope text NOT NULL,
    key text NOT NULL,
    request_digest character(64) NOT NULL,
    batch_id uuid NOT NULL,
    result_document bytea NOT NULL,
    result_digest character(64) NOT NULL,
    caller_identity text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_ingestion_processing_command PRIMARY KEY (scope, key),
    CONSTRAINT fk_ingestion_processing_command_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_ingestion_processing_command_scope CHECK (length(scope) BETWEEN 1 AND 150),
    CONSTRAINT ck_ingestion_processing_command_key CHECK (length(key) BETWEEN 1 AND 200),
    CONSTRAINT ck_ingestion_processing_command_request_digest CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_ingestion_processing_command_result CHECK (octet_length(result_document) > 0),
    CONSTRAINT ck_ingestion_processing_command_result_digest CHECK (result_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_ingestion_processing_command_caller CHECK (length(caller_identity) BETWEEN 1 AND 200)
);

CREATE OR REPLACE FUNCTION processing.enforce_import_batch_transition()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    transition_allowed boolean;
BEGIN
    IF OLD.id <> NEW.id
       OR OLD.producer_identity <> NEW.producer_identity
       OR OLD.producer_build <> NEW.producer_build
       OR OLD.collector_export_id <> NEW.collector_export_id
       OR OLD.collector_export_digest <> NEW.collector_export_digest
       OR OLD.target_site_key <> NEW.target_site_key
       OR OLD.target_catalog_key <> NEW.target_catalog_key
       OR OLD.target_catalog_configuration_revision_id <> NEW.target_catalog_configuration_revision_id
       OR OLD.expected_item_count <> NEW.expected_item_count
       OR OLD.manifest_digest <> NEW.manifest_digest
       OR OLD.item_index_digest <> NEW.item_index_digest
       OR OLD.payload_digest <> NEW.payload_digest
       OR OLD.payload_object_key <> NEW.payload_object_key
       OR OLD.payload_object_digest <> NEW.payload_object_digest
       OR OLD.payload_object_size <> NEW.payload_object_size
       OR OLD.payload_content_type <> NEW.payload_content_type
       OR OLD.registered_at_utc <> NEW.registered_at_utc
    THEN
        RAISE EXCEPTION 'immutable Ingestion batch identity may not change'
            USING ERRCODE = '55000';
    END IF;

    IF NEW.aggregate_revision <> OLD.aggregate_revision + 1
    THEN
        RAISE EXCEPTION 'Ingestion batch aggregate revision must advance exactly once'
            USING ERRCODE = '40001';
    END IF;

    transition_allowed :=
        (OLD.state = 1 AND NEW.state IN (2, 17, 18)) OR
        (OLD.state = 2 AND NEW.state IN (3, 17, 18)) OR
        (OLD.state = 3 AND NEW.state IN (4, 17, 18)) OR
        (OLD.state = 4 AND NEW.state IN (5, 13, 14, 15, 17, 18)) OR
        (OLD.state = 5 AND NEW.state IN (6, 17, 18)) OR
        (OLD.state = 6 AND NEW.state IN (7, 8, 17, 18)) OR
        (OLD.state = 7 AND NEW.state IN (8, 17, 18)) OR
        (OLD.state = 8 AND NEW.state IN (9, 17, 18)) OR
        (OLD.state = 9 AND NEW.state IN (10, 11, 16)) OR
        (OLD.state IN (10, 11) AND NEW.state = 12);
    IF NOT transition_allowed
    THEN
        RAISE EXCEPTION 'invalid Ingestion batch transition % -> %', OLD.state, NEW.state
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_ingestion_batch_transition ON batches.import_batch;
CREATE TRIGGER trg_ingestion_batch_transition
    BEFORE UPDATE ON batches.import_batch
    FOR EACH ROW EXECUTE FUNCTION processing.enforce_import_batch_transition();

CREATE OR REPLACE FUNCTION processing.reject_immutable_processing_record()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'immutable Ingestion processing record % may not be updated or deleted', TG_TABLE_NAME
        USING ERRCODE = '55000';
END;
$$;

CREATE TRIGGER trg_ingestion_item_decision_immutable
    BEFORE UPDATE OR DELETE ON processing.item_decision
    FOR EACH ROW EXECUTE FUNCTION processing.reject_immutable_processing_record();

CREATE TRIGGER trg_ingestion_processing_command_immutable
    BEFORE UPDATE OR DELETE ON processing_operations.command_result
    FOR EACH ROW EXECUTE FUNCTION processing.reject_immutable_processing_record();

CREATE OR REPLACE FUNCTION processing.enforce_catalog_delivery_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.delivery_id <> NEW.delivery_id
       OR OLD.batch_id <> NEW.batch_id
       OR OLD.item_key <> NEW.item_key
       OR OLD.command_type <> NEW.command_type
       OR OLD.command_document <> NEW.command_document
       OR OLD.command_digest <> NEW.command_digest
       OR OLD.created_at_utc <> NEW.created_at_utc
    THEN
        RAISE EXCEPTION 'immutable Ingestion Catalog delivery identity may not change'
            USING ERRCODE = '55000';
    END IF;

    IF OLD.state IN (3, 4) AND ROW(OLD.*) IS DISTINCT FROM ROW(NEW.*)
    THEN
        RAISE EXCEPTION 'terminal Ingestion Catalog delivery may not change'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_ingestion_catalog_delivery_mutation
    BEFORE UPDATE ON processing.catalog_delivery
    FOR EACH ROW EXECUTE FUNCTION processing.enforce_catalog_delivery_mutation();

CREATE OR REPLACE FUNCTION processing.reject_catalog_delivery_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Ingestion Catalog delivery rows may not be deleted'
        USING ERRCODE = '55000';
END;
$$;

CREATE TRIGGER trg_ingestion_catalog_delivery_no_delete
    BEFORE DELETE ON processing.catalog_delivery
    FOR EACH ROW EXECUTE FUNCTION processing.reject_catalog_delivery_delete();
