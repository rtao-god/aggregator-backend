DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM access_projection.listing_eligibility_projection
        LIMIT 1)
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7201',
            MESSAGE = 'listing_eligibility_projection contains rows without producer inbox lineage',
            HINT = 'Clear the rebuildable Promotion eligibility projection and replay Catalog listing eligibility events.';
    END IF;
END
$$;

CREATE TABLE messaging.inbox_message
(
    message_id uuid PRIMARY KEY,
    contract_identity varchar(256) NOT NULL,
    payload_digest char(64) NOT NULL,
    catalog_key varchar(120) NOT NULL,
    listing_id uuid NOT NULL,
    source_revision bigint NOT NULL,
    projection_digest char(64) NOT NULL,
    correlation_id varchar(128) NOT NULL,
    causation_id uuid NULL,
    received_at_utc timestamptz NOT NULL,
    processed_at_utc timestamptz NOT NULL,
    processing_state varchar(32) NOT NULL,
    CONSTRAINT ck_promotion_inbox_message_id CHECK (
        message_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_inbox_contract CHECK (
        length(btrim(contract_identity)) > 0),
    CONSTRAINT ck_promotion_inbox_payload_digest CHECK (
        payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_promotion_inbox_catalog_key CHECK (
        catalog_key ~ '^[a-z0-9]+(?:-[a-z0-9]+)*$'),
    CONSTRAINT ck_promotion_inbox_listing_id CHECK (
        listing_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_inbox_source_revision CHECK (source_revision > 0),
    CONSTRAINT ck_promotion_inbox_projection_digest CHECK (
        projection_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_promotion_inbox_correlation CHECK (
        length(btrim(correlation_id)) > 0),
    CONSTRAINT ck_promotion_inbox_causation CHECK (
        causation_id IS NULL OR
        causation_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_inbox_time CHECK (
        processed_at_utc >= received_at_utc),
    CONSTRAINT ck_promotion_inbox_state CHECK (
        processing_state = 'applied')
);

CREATE UNIQUE INDEX ux_promotion_inbox_listing_revision
    ON messaging.inbox_message (catalog_key, listing_id, source_revision);

CREATE INDEX ix_promotion_inbox_received
    ON messaging.inbox_message (received_at_utc, message_id);

ALTER TABLE access_projection.listing_eligibility_projection
    ADD COLUMN published_listing_revision_id uuid NULL,
    ADD COLUMN source_message_id uuid NOT NULL,
    ADD COLUMN source_contract_identity varchar(256) NOT NULL,
    ADD COLUMN source_payload_digest char(64) NOT NULL,
    ADD COLUMN projection_digest char(64) NOT NULL,
    ADD COLUMN correlation_id varchar(128) NOT NULL,
    ADD COLUMN causation_id uuid NULL,
    ADD COLUMN received_at_utc timestamptz NOT NULL;

ALTER TABLE access_projection.listing_eligibility_projection
    DROP CONSTRAINT ck_promotion_eligibility_state,
    DROP CONSTRAINT ck_promotion_eligibility_contacts,
    DROP CONSTRAINT ck_promotion_eligibility_categories;

ALTER TABLE access_projection.listing_eligibility_projection
    ADD CONSTRAINT ck_promotion_eligibility_state CHECK (
        NOT (is_archived AND is_published)),
    ADD CONSTRAINT ck_promotion_eligibility_publication_identity CHECK (
        is_published = (published_listing_revision_id IS NOT NULL)),
    ADD CONSTRAINT ck_promotion_eligibility_contacts CHECK (
        jsonb_typeof(contact_capabilities_json) = 'array' AND
        has_verified_contact = (jsonb_array_length(contact_capabilities_json) > 0)),
    ADD CONSTRAINT ck_promotion_eligibility_categories CHECK (
        jsonb_typeof(category_keys_json) = 'array' AND
        (NOT is_published OR jsonb_array_length(category_keys_json) > 0)),
    ADD CONSTRAINT ck_promotion_eligibility_unpublished_shape CHECK (
        is_published OR (
            NOT has_verified_contact AND
            jsonb_array_length(contact_capabilities_json) = 0 AND
            jsonb_array_length(category_keys_json) = 0 AND
            district_key IS NULL)),
    ADD CONSTRAINT ck_promotion_eligibility_source_message CHECK (
        source_message_id <> '00000000-0000-0000-0000-000000000000'),
    ADD CONSTRAINT ck_promotion_eligibility_source_payload_digest CHECK (
        source_payload_digest ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_promotion_eligibility_projection_digest CHECK (
        projection_digest ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_promotion_eligibility_correlation CHECK (
        length(btrim(correlation_id)) > 0),
    ADD CONSTRAINT ck_promotion_eligibility_causation CHECK (
        causation_id IS NULL OR
        causation_id <> '00000000-0000-0000-0000-000000000000'),
    ADD CONSTRAINT ck_promotion_eligibility_received_time CHECK (
        received_at_utc >= changed_at_utc),
    ADD CONSTRAINT fk_promotion_eligibility_source_message
        FOREIGN KEY (source_message_id)
        REFERENCES messaging.inbox_message (message_id)
        ON DELETE RESTRICT;

CREATE UNIQUE INDEX ux_promotion_eligibility_source_message
    ON access_projection.listing_eligibility_projection (source_message_id);
