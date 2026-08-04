CREATE SCHEMA IF NOT EXISTS promotion;

CREATE TABLE promotion.overlay_activation_sequence
(
    catalog_key varchar(96) PRIMARY KEY,
    next_revision bigint NOT NULL,
    CONSTRAINT overlay_activation_sequence_catalog_key_shape
        CHECK (catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT overlay_activation_sequence_positive
        CHECK (next_revision >= 2)
);

CREATE TABLE promotion.overlay_publication
(
    overlay_id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE,
    catalog_key varchar(96) NOT NULL,
    source_public_read_revision_id uuid NOT NULL,
    activation_revision bigint NOT NULL,
    content_digest char(64) NOT NULL,
    created_at_utc timestamptz NOT NULL,
    CONSTRAINT overlay_publication_catalog_key_shape
        CHECK (catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT overlay_publication_activation_revision_positive
        CHECK (activation_revision >= 1),
    CONSTRAINT overlay_publication_digest_shape
        CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT overlay_publication_catalog_activation_unique
        UNIQUE (catalog_key, activation_revision),
    CONSTRAINT overlay_publication_catalog_digest_unique
        UNIQUE (catalog_key, content_digest)
);

CREATE INDEX overlay_publication_source_revision_idx
    ON promotion.overlay_publication
    (catalog_key, source_public_read_revision_id, activation_revision DESC);

CREATE TABLE promotion.overlay_item
(
    overlay_id uuid NOT NULL
        REFERENCES promotion.overlay_publication(overlay_id) ON DELETE CASCADE,
    listing_id uuid NOT NULL,
    campaign_id uuid NOT NULL,
    position integer NOT NULL,
    locale varchar(35) NOT NULL,
    title varchar(300) NOT NULL,
    route_path varchar(500) NOT NULL,
    disclosure_label varchar(100) NOT NULL,
    PRIMARY KEY (overlay_id, position),
    CONSTRAINT overlay_item_listing_unique UNIQUE (overlay_id, listing_id),
    CONSTRAINT overlay_item_position_bounds CHECK (position BETWEEN 1 AND 100),
    CONSTRAINT overlay_item_listing_nonempty
        CHECK (listing_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT overlay_item_campaign_nonempty
        CHECK (campaign_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT overlay_item_route_shape
        CHECK (left(route_path, 1) = '/' AND position('..' IN route_path) = 0),
    CONSTRAINT overlay_item_text_nonempty
        CHECK
        (
            length(trim(locale)) > 0
            AND length(trim(title)) > 0
            AND length(trim(disclosure_label)) > 0
        )
);

CREATE TABLE promotion.current_overlay
(
    catalog_key varchar(96) PRIMARY KEY,
    overlay_id uuid NOT NULL
        REFERENCES promotion.overlay_publication(overlay_id),
    source_public_read_revision_id uuid NOT NULL,
    activation_revision bigint NOT NULL,
    activated_at_utc timestamptz NOT NULL,
    CONSTRAINT current_overlay_catalog_key_shape
        CHECK (catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT current_overlay_activation_revision_positive
        CHECK (activation_revision >= 1)
);

CREATE TABLE promotion.overlay_command
(
    command_id uuid PRIMARY KEY,
    command_digest char(64) NOT NULL,
    overlay_id uuid NOT NULL UNIQUE
        REFERENCES promotion.overlay_publication(overlay_id),
    committed_at_utc timestamptz NOT NULL,
    CONSTRAINT overlay_command_digest_shape
        CHECK (command_digest ~ '^[0-9a-f]{64}$')
);

CREATE TABLE promotion.overlay_outbox
(
    event_id uuid PRIMARY KEY,
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
    CONSTRAINT overlay_outbox_payload_digest_shape
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT overlay_outbox_delivery_attempts_nonnegative
        CHECK (delivery_attempts >= 0),
    CONSTRAINT overlay_outbox_lease_shape
        CHECK
        (
            (lease_token IS NULL AND leased_by IS NULL AND lease_expires_at_utc IS NULL)
            OR
            (lease_token IS NOT NULL AND leased_by IS NOT NULL AND lease_expires_at_utc IS NOT NULL)
        ),
    CONSTRAINT overlay_outbox_terminal_shape
        CHECK
        (
            NOT (dispatched_at_utc IS NOT NULL AND dead_lettered_at_utc IS NOT NULL)
        )
);

CREATE INDEX overlay_outbox_dispatch_idx
    ON promotion.overlay_outbox
    (dispatched_at_utc, dead_lettered_at_utc, occurred_at_utc, event_id);
CREATE INDEX overlay_outbox_lease_idx
    ON promotion.overlay_outbox (lease_expires_at_utc)
    WHERE dispatched_at_utc IS NULL AND dead_lettered_at_utc IS NULL;
