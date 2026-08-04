CREATE TABLE operations.package_validation_work
(
    batch_id uuid PRIMARY KEY,
    status integer NOT NULL,
    attempt_count integer NOT NULL,
    claim_id uuid NULL,
    worker_identity text NULL,
    leased_until_utc timestamp with time zone NULL,
    last_failure_code text NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT fk_package_validation_work_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_package_validation_work_status
        CHECK (status BETWEEN 1 AND 4),
    CONSTRAINT ck_package_validation_work_attempt_count
        CHECK (attempt_count >= 0),
    CONSTRAINT ck_package_validation_work_claim
        CHECK (
            (status = 2
             AND claim_id IS NOT NULL
             AND claim_id <> '00000000-0000-0000-0000-000000000000'::uuid
             AND worker_identity IS NOT NULL
             AND length(worker_identity) BETWEEN 1 AND 200
             AND leased_until_utc IS NOT NULL)
            OR
            (status <> 2
             AND claim_id IS NULL
             AND worker_identity IS NULL
             AND leased_until_utc IS NULL)),
    CONSTRAINT ck_package_validation_work_failure
        CHECK (
            (status = 4 AND last_failure_code IS NOT NULL AND length(last_failure_code) BETWEEN 1 AND 200)
            OR
            (status <> 4 AND last_failure_code IS NULL)),
    CONSTRAINT ck_package_validation_work_time_order
        CHECK (updated_at_utc >= created_at_utc)
);

CREATE INDEX ix_package_validation_work_claimable
    ON operations.package_validation_work (status, leased_until_utc, updated_at_utc);

CREATE TABLE batches.ingestion_item
(
    batch_id uuid NOT NULL,
    item_key text NOT NULL,
    ordinal integer NOT NULL,
    entity_kind integer NOT NULL,
    content_digest character(64) NOT NULL,
    canonical_document bytea NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_ingestion_item PRIMARY KEY (batch_id, item_key),
    CONSTRAINT uq_ingestion_item_ordinal UNIQUE (batch_id, ordinal),
    CONSTRAINT fk_ingestion_item_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_ingestion_item_key
        CHECK (length(item_key) BETWEEN 1 AND 300 AND item_key !~ '[[:cntrl:]]'),
    CONSTRAINT ck_ingestion_item_ordinal
        CHECK (ordinal >= 0),
    CONSTRAINT ck_ingestion_item_entity_kind
        CHECK (entity_kind BETWEEN 1 AND 2),
    CONSTRAINT ck_ingestion_item_digest
        CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_ingestion_item_document
        CHECK (octet_length(canonical_document) > 0)
);

CREATE TABLE batches.item_issue
(
    issue_id uuid PRIMARY KEY,
    batch_id uuid NOT NULL,
    item_key text NOT NULL,
    code text NOT NULL,
    severity integer NOT NULL,
    detail text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT uq_item_issue_identity UNIQUE (batch_id, item_key, code, detail),
    CONSTRAINT fk_item_issue_item
        FOREIGN KEY (batch_id, item_key)
        REFERENCES batches.ingestion_item (batch_id, item_key)
        ON DELETE RESTRICT,
    CONSTRAINT ck_item_issue_id
        CHECK (issue_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_item_issue_code
        CHECK (code ~ '^[a-z0-9][a-z0-9._-]{0,199}$'),
    CONSTRAINT ck_item_issue_severity
        CHECK (severity BETWEEN 1 AND 3),
    CONSTRAINT ck_item_issue_detail
        CHECK (length(detail) BETWEEN 1 AND 2000 AND detail !~ '[[:cntrl:]]')
);

CREATE INDEX ix_item_issue_batch_item
    ON batches.item_issue (batch_id, item_key, severity, code);

CREATE TRIGGER trg_ingestion_item_immutable
    BEFORE UPDATE OR DELETE ON batches.ingestion_item
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();

CREATE TRIGGER trg_item_issue_immutable
    BEFORE UPDATE OR DELETE ON batches.item_issue
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();
