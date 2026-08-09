CREATE SCHEMA IF NOT EXISTS messaging;

CREATE TABLE messaging.catalog_configuration_inbox
(
    message_id uuid PRIMARY KEY,
    routing_key text NOT NULL,
    contract_identity text NOT NULL,
    payload_digest character(64) NOT NULL,
    site_key text NOT NULL,
    catalog_key text NOT NULL,
    configuration_revision_id uuid NOT NULL,
    previous_configuration_revision_id uuid NULL,
    aggregate_revision bigint NOT NULL,
    correlation_id text NOT NULL,
    received_at_utc timestamp with time zone NOT NULL,
    processed_at_utc timestamp with time zone NOT NULL,
    projection_digest character(64) NOT NULL,
    CONSTRAINT uq_catalog_configuration_inbox_revision
        UNIQUE (catalog_key, aggregate_revision),
    CONSTRAINT ck_catalog_configuration_inbox_message_id
        CHECK (message_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_configuration_inbox_routing_key
        CHECK (routing_key = 'catalog.configuration.activated'),
    CONSTRAINT ck_catalog_configuration_inbox_contract_identity
        CHECK (contract_identity = 'aggregator.catalog.configuration-activated@1'),
    CONSTRAINT ck_catalog_configuration_inbox_payload_digest
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_configuration_inbox_site_key
        CHECK (
            site_key ~ '^[a-z][a-z0-9-]{0,95}$'
            AND site_key NOT LIKE '%--%'
            AND right(site_key, 1) <> '-'),
    CONSTRAINT ck_catalog_configuration_inbox_catalog_key
        CHECK (
            catalog_key ~ '^[a-z][a-z0-9-]{0,95}$'
            AND catalog_key NOT LIKE '%--%'
            AND right(catalog_key, 1) <> '-'),
    CONSTRAINT ck_catalog_configuration_inbox_configuration_id
        CHECK (configuration_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_configuration_inbox_previous_configuration_id
        CHECK (
            previous_configuration_revision_id IS NULL
            OR previous_configuration_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_configuration_inbox_revision_chain
        CHECK (
            (aggregate_revision = 1 AND previous_configuration_revision_id IS NULL)
            OR (aggregate_revision > 1 AND previous_configuration_revision_id IS NOT NULL)),
    CONSTRAINT ck_catalog_configuration_inbox_correlation
        CHECK (length(correlation_id) BETWEEN 8 AND 128),
    CONSTRAINT ck_catalog_configuration_inbox_time_order
        CHECK (processed_at_utc >= received_at_utc),
    CONSTRAINT ck_catalog_configuration_inbox_projection_digest
        CHECK (projection_digest ~ '^[0-9a-f]{64}$')
);

DO
$$
BEGIN
    IF EXISTS (SELECT 1 FROM catalog_projection.catalog_reference) THEN
        RAISE EXCEPTION
            'INGESTION_CATALOG_PROJECTION_REBUILD_REQUIRED: catalog_projection.catalog_reference contains rows without producer-event lineage.'
            USING HINT = 'Replay CatalogConfigurationActivated events into an empty Ingestion Catalog projection before applying this migration.';
    END IF;
END
$$;

ALTER TABLE catalog_projection.catalog_reference
    ADD COLUMN configuration_digest character(64) NOT NULL,
    ADD COLUMN market_area_key text NOT NULL,
    ADD COLUMN source_event_id uuid NOT NULL,
    ADD COLUMN source_payload_digest character(64) NOT NULL,
    ADD COLUMN activated_at_utc timestamp with time zone NOT NULL,
    ADD COLUMN projection_digest character(64) NOT NULL;

ALTER TABLE catalog_projection.catalog_reference
    DROP CONSTRAINT ck_catalog_reference_listing_kinds,
    ADD CONSTRAINT uq_catalog_reference_catalog_key UNIQUE (catalog_key),
    ADD CONSTRAINT uq_catalog_reference_source_event UNIQUE (source_event_id),
    ADD CONSTRAINT fk_catalog_reference_source_event
        FOREIGN KEY (source_event_id)
        REFERENCES messaging.catalog_configuration_inbox(message_id)
        ON DELETE RESTRICT,
    ADD CONSTRAINT ck_catalog_reference_configuration_digest
        CHECK (configuration_digest ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_catalog_reference_market_area_key
        CHECK (
            market_area_key ~ '^[a-z][a-z0-9-]{0,95}$'
            AND market_area_key NOT LIKE '%--%'
            AND right(market_area_key, 1) <> '-'),
    ADD CONSTRAINT ck_catalog_reference_listing_kinds
        CHECK (
            supported_listing_kinds = ARRAY[2]::integer[]
            OR supported_listing_kinds = ARRAY[3]::integer[]
            OR supported_listing_kinds = ARRAY[2, 3]::integer[]),
    ADD CONSTRAINT ck_catalog_reference_source_payload_digest
        CHECK (source_payload_digest ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_catalog_reference_projection_digest
        CHECK (projection_digest ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_catalog_reference_time_order
        CHECK (updated_at_utc >= activated_at_utc);

CREATE INDEX ix_catalog_configuration_inbox_received
    ON messaging.catalog_configuration_inbox(received_at_utc, message_id);
