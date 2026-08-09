DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM access_projection.public_read_reference)
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Analytics Query public-reference migration is blocked by existing public-read projections.',
            HINT = 'Rebuild the Analytics public-reference projection from the complete Query activation stream after applying V003 to an empty projection state.';
    END IF;
END
$$;

ALTER TABLE access_projection.public_read_reference
    ADD COLUMN activation_revision bigint NOT NULL,
    ADD COLUMN projection_digest char(64) NOT NULL,
    ADD CONSTRAINT ck_analytics_public_read_activation_revision CHECK
    (
        activation_revision > 0
    ),
    ADD CONSTRAINT ck_analytics_public_read_projection_digest CHECK
    (
        projection_digest ~ '^[0-9a-f]{64}$'
    );

CREATE UNIQUE INDEX ux_analytics_public_read_catalog_activation_revision
    ON access_projection.public_read_reference (catalog_key, activation_revision);

CREATE TABLE access_projection.public_sponsored_placement_reference
(
    public_read_revision_id uuid NOT NULL,
    placement_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    scope_type integer NOT NULL,
    scope_key varchar(200) NOT NULL,
    starts_at_utc timestamptz NOT NULL,
    hard_expiry_at_utc timestamptz NOT NULL,
    PRIMARY KEY (public_read_revision_id, placement_id),
    CONSTRAINT ux_analytics_public_sponsored_placement_identity
        UNIQUE (public_read_revision_id, placement_id, listing_id),
    CONSTRAINT ck_analytics_public_placement_ids CHECK
    (
        placement_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_public_placement_scope CHECK
    (
        scope_type BETWEEN 1 AND 4
        AND length(btrim(scope_key)) > 0
    ),
    CONSTRAINT ck_analytics_public_placement_interval CHECK
    (
        starts_at_utc < hard_expiry_at_utc
    ),
    CONSTRAINT fk_analytics_public_placement_revision
        FOREIGN KEY (public_read_revision_id)
        REFERENCES access_projection.public_read_reference (public_read_revision_id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_analytics_public_placement_listing
        FOREIGN KEY (public_read_revision_id, listing_id)
        REFERENCES access_projection.public_listing_reference
            (public_read_revision_id, listing_id)
        ON DELETE RESTRICT
);

CREATE INDEX ix_analytics_public_sponsored_placement_listing
    ON access_projection.public_sponsored_placement_reference
       (public_read_revision_id, listing_id);

ALTER TABLE events.interaction_event
    ADD CONSTRAINT fk_analytics_interaction_sponsored_placement
    FOREIGN KEY (public_read_revision_id, placement_id, listing_id)
    REFERENCES access_projection.public_sponsored_placement_reference
        (public_read_revision_id, placement_id, listing_id)
    ON DELETE RESTRICT;

CREATE TABLE access_projection.public_read_activation_checkpoint
(
    catalog_key varchar(100) PRIMARY KEY,
    activation_revision bigint NOT NULL,
    public_read_revision_id uuid NOT NULL,
    projection_digest char(64) NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_analytics_public_checkpoint_catalog_key CHECK
    (
        catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'
    ),
    CONSTRAINT ck_analytics_public_checkpoint_revision CHECK
    (
        activation_revision > 0
    ),
    CONSTRAINT ck_analytics_public_checkpoint_digest CHECK
    (
        projection_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT fk_analytics_public_checkpoint_revision
        FOREIGN KEY (public_read_revision_id)
        REFERENCES access_projection.public_read_reference (public_read_revision_id)
        ON DELETE RESTRICT
);

CREATE SCHEMA messaging;

CREATE TABLE messaging.inbox_message
(
    message_id uuid PRIMARY KEY,
    catalog_key varchar(100) NOT NULL,
    routing_key varchar(200) NOT NULL,
    contract_identity varchar(200) NOT NULL,
    payload_digest char(64) NOT NULL,
    activation_revision bigint NOT NULL,
    public_read_revision_id uuid NOT NULL,
    received_at_utc timestamptz NOT NULL,
    correlation_id varchar(128) NOT NULL,
    disposition integer NOT NULL,
    result_projection_digest char(64) NOT NULL,
    processed_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_analytics_inbox_message_id CHECK
    (
        message_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_inbox_catalog_key CHECK
    (
        catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'
    ),
    CONSTRAINT ck_analytics_inbox_routing_key CHECK
    (
        length(btrim(routing_key)) > 0
    ),
    CONSTRAINT ck_analytics_inbox_contract_identity CHECK
    (
        length(btrim(contract_identity)) > 0
    ),
    CONSTRAINT ck_analytics_inbox_payload_digest CHECK
    (
        payload_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT ck_analytics_inbox_activation_revision CHECK
    (
        activation_revision > 0
    ),
    CONSTRAINT ck_analytics_inbox_correlation_id CHECK
    (
        length(btrim(correlation_id)) > 0
    ),
    CONSTRAINT ck_analytics_inbox_disposition CHECK
    (
        disposition BETWEEN 1 AND 3
    ),
    CONSTRAINT ck_analytics_inbox_result_digest CHECK
    (
        result_projection_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT ck_analytics_inbox_processing_time CHECK
    (
        processed_at_utc >= received_at_utc
    ),
    CONSTRAINT fk_analytics_inbox_public_read_revision
        FOREIGN KEY (public_read_revision_id)
        REFERENCES access_projection.public_read_reference (public_read_revision_id)
        ON DELETE RESTRICT
);

CREATE INDEX ix_analytics_inbox_catalog_activation_revision
    ON messaging.inbox_message (catalog_key, activation_revision, message_id);
