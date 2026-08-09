DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM contracts.producer_registration) THEN
        RAISE EXCEPTION
            'producer_registration contains legacy rows without revision and command lineage'
            USING ERRCODE = '55000',
                  HINT = 'Remove manual registrations and recreate them through the Ingestion producer-registration command after migration.';
    END IF;
END
$$;

ALTER TABLE contracts.producer_registration
    ADD COLUMN aggregate_revision bigint,
    ADD COLUMN content_digest character(64),
    ADD COLUMN updated_by_service_identity text,
    ADD COLUMN reason text;

ALTER TABLE contracts.producer_registration
    ALTER COLUMN aggregate_revision SET NOT NULL,
    ALTER COLUMN content_digest SET NOT NULL,
    ALTER COLUMN updated_by_service_identity SET NOT NULL,
    ALTER COLUMN reason SET NOT NULL;

ALTER TABLE contracts.producer_registration
    ADD CONSTRAINT ck_producer_registration_aggregate_revision
        CHECK (aggregate_revision > 0),
    ADD CONSTRAINT ck_producer_registration_content_digest
        CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_producer_registration_updated_by
        CHECK (length(updated_by_service_identity) BETWEEN 1 AND 200),
    ADD CONSTRAINT ck_producer_registration_reason
        CHECK (length(reason) BETWEEN 8 AND 1000);

CREATE TABLE contracts.producer_registration_revision
(
    producer_identity text NOT NULL,
    aggregate_revision bigint NOT NULL,
    active boolean NOT NULL,
    supported_contract_revisions integer[] NOT NULL,
    content_digest character(64) NOT NULL,
    changed_by_service_identity text NOT NULL,
    reason text NOT NULL,
    changed_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_producer_registration_revision
        PRIMARY KEY (producer_identity, aggregate_revision),
    CONSTRAINT uq_producer_registration_revision_digest
        UNIQUE (producer_identity, content_digest),
    CONSTRAINT ck_producer_registration_revision_identity
        CHECK (length(producer_identity) BETWEEN 1 AND 200),
    CONSTRAINT ck_producer_registration_revision_positive
        CHECK (aggregate_revision > 0),
    CONSTRAINT ck_producer_registration_revision_contracts
        CHECK (
            cardinality(supported_contract_revisions) > 0
            AND 0 < ALL (supported_contract_revisions)),
    CONSTRAINT ck_producer_registration_revision_digest
        CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_producer_registration_revision_changed_by
        CHECK (length(changed_by_service_identity) BETWEEN 1 AND 200),
    CONSTRAINT ck_producer_registration_revision_reason
        CHECK (length(reason) BETWEEN 8 AND 1000)
);

ALTER TABLE contracts.producer_registration
    ADD CONSTRAINT fk_producer_registration_current_revision
        FOREIGN KEY (identity, aggregate_revision)
        REFERENCES contracts.producer_registration_revision
            (producer_identity, aggregate_revision)
        DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE operations.producer_registration_command
(
    scope text NOT NULL,
    key text NOT NULL,
    request_digest character(64) NOT NULL,
    producer_identity text NOT NULL,
    result_document bytea NOT NULL,
    result_digest character(64) NOT NULL,
    caller_service_identity text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_producer_registration_command PRIMARY KEY (scope, key),
    CONSTRAINT fk_producer_registration_command_producer
        FOREIGN KEY (producer_identity)
        REFERENCES contracts.producer_registration (identity)
        DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT ck_producer_registration_command_scope
        CHECK (length(scope) BETWEEN 1 AND 150),
    CONSTRAINT ck_producer_registration_command_key
        CHECK (length(key) BETWEEN 1 AND 200),
    CONSTRAINT ck_producer_registration_command_request_digest
        CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_producer_registration_command_result_document
        CHECK (octet_length(result_document) > 0),
    CONSTRAINT ck_producer_registration_command_result_digest
        CHECK (result_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_producer_registration_command_caller
        CHECK (length(caller_service_identity) BETWEEN 1 AND 200)
);

CREATE TRIGGER trg_producer_registration_revision_immutable
    BEFORE UPDATE OR DELETE ON contracts.producer_registration_revision
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();

CREATE TRIGGER trg_producer_registration_command_immutable
    BEFORE UPDATE OR DELETE ON operations.producer_registration_command
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();
