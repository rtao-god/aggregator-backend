CREATE SCHEMA IF NOT EXISTS catalog_ingestion;

CREATE TABLE catalog_ingestion.catalog_target
(
    site_key text NOT NULL,
    catalog_key text NOT NULL,
    active_configuration_revision_id uuid NOT NULL,
    projection_revision bigint NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_catalog_ingestion_target PRIMARY KEY (site_key, catalog_key),
    CONSTRAINT ck_catalog_ingestion_target_site_key CHECK (site_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_catalog_ingestion_target_catalog_key CHECK (catalog_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_catalog_ingestion_target_configuration CHECK (active_configuration_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_ingestion_target_revision CHECK (projection_revision > 0)
);

CREATE TABLE catalog_ingestion.draft_proposal
(
    listing_id uuid PRIMARY KEY,
    listing_revision_id uuid NOT NULL,
    ingestion_batch_id uuid NOT NULL,
    ingestion_item_key text NOT NULL,
    site_key text NOT NULL,
    catalog_key text NOT NULL,
    catalog_configuration_revision_id uuid NOT NULL,
    entity_kind text NOT NULL,
    subject_natural_key text NOT NULL,
    fields_document bytea NOT NULL,
    fields_digest character(64) NOT NULL,
    aggregate_revision bigint NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    last_changed_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT uq_catalog_ingestion_draft_subject UNIQUE (catalog_key, entity_kind, subject_natural_key),
    CONSTRAINT uq_catalog_ingestion_draft_source UNIQUE (ingestion_batch_id, ingestion_item_key),
    CONSTRAINT ck_catalog_ingestion_draft_listing CHECK (listing_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_ingestion_draft_revision_id CHECK (listing_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_ingestion_draft_batch CHECK (ingestion_batch_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_ingestion_draft_item CHECK (length(ingestion_item_key) BETWEEN 1 AND 200),
    CONSTRAINT ck_catalog_ingestion_draft_site CHECK (site_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_catalog_ingestion_draft_catalog CHECK (catalog_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_catalog_ingestion_draft_configuration CHECK (catalog_configuration_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_ingestion_draft_entity CHECK (entity_kind IN ('place', 'provider')),
    CONSTRAINT ck_catalog_ingestion_draft_natural_key CHECK (length(subject_natural_key) BETWEEN 1 AND 300),
    CONSTRAINT ck_catalog_ingestion_draft_document CHECK (octet_length(fields_document) > 0),
    CONSTRAINT ck_catalog_ingestion_draft_digest CHECK (fields_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_ingestion_draft_revision CHECK (aggregate_revision > 0),
    CONSTRAINT ck_catalog_ingestion_draft_time CHECK (last_changed_at_utc >= created_at_utc)
);

CREATE TABLE catalog_ingestion.command_result
(
    command_id uuid PRIMARY KEY,
    command_digest character(64) NOT NULL,
    ingestion_batch_id uuid NOT NULL,
    ingestion_item_key text NOT NULL,
    result_document bytea NOT NULL,
    result_digest character(64) NOT NULL,
    caller_identity text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT ck_catalog_ingestion_command_id CHECK (command_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_ingestion_command_digest CHECK (command_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_ingestion_command_batch CHECK (ingestion_batch_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_ingestion_command_item CHECK (length(ingestion_item_key) BETWEEN 1 AND 200),
    CONSTRAINT ck_catalog_ingestion_command_result CHECK (octet_length(result_document) > 0),
    CONSTRAINT ck_catalog_ingestion_command_result_digest CHECK (result_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_ingestion_command_caller CHECK (length(caller_identity) BETWEEN 1 AND 200)
);

CREATE INDEX ix_catalog_ingestion_command_source
    ON catalog_ingestion.command_result (ingestion_batch_id, ingestion_item_key);

CREATE OR REPLACE FUNCTION catalog_ingestion.enforce_draft_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.listing_id <> NEW.listing_id
       OR OLD.site_key <> NEW.site_key
       OR OLD.catalog_key <> NEW.catalog_key
       OR OLD.catalog_configuration_revision_id <> NEW.catalog_configuration_revision_id
       OR OLD.entity_kind <> NEW.entity_kind
       OR OLD.subject_natural_key <> NEW.subject_natural_key
       OR OLD.created_at_utc <> NEW.created_at_utc
    THEN
        RAISE EXCEPTION 'immutable Catalog ingestion draft identity may not change'
            USING ERRCODE = '55000';
    END IF;

    IF NEW.aggregate_revision <> OLD.aggregate_revision + 1
    THEN
        RAISE EXCEPTION 'Catalog ingestion draft revision must advance exactly once'
            USING ERRCODE = '40001';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_catalog_ingestion_draft_mutation
    BEFORE UPDATE ON catalog_ingestion.draft_proposal
    FOR EACH ROW EXECUTE FUNCTION catalog_ingestion.enforce_draft_mutation();

CREATE OR REPLACE FUNCTION catalog_ingestion.reject_immutable_command_result()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Catalog ingestion command outcomes are immutable'
        USING ERRCODE = '55000';
END;
$$;

CREATE TRIGGER trg_catalog_ingestion_command_result_immutable
    BEFORE UPDATE OR DELETE ON catalog_ingestion.command_result
    FOR EACH ROW EXECUTE FUNCTION catalog_ingestion.reject_immutable_command_result();

CREATE OR REPLACE FUNCTION catalog_ingestion.reject_draft_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Catalog ingestion draft proposals may not be deleted; supersede them through Catalog editorial workflow'
        USING ERRCODE = '55000';
END;
$$;

CREATE TRIGGER trg_catalog_ingestion_draft_no_delete
    BEFORE DELETE ON catalog_ingestion.draft_proposal
    FOR EACH ROW EXECUTE FUNCTION catalog_ingestion.reject_draft_delete();
