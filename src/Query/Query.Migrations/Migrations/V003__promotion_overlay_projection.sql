CREATE TABLE query.promotion_overlay_revision
(
    overlay_id uuid PRIMARY KEY,
    catalog_key varchar(96) NOT NULL,
    source_public_read_revision_id uuid NOT NULL,
    activation_revision bigint NOT NULL,
    content_digest char(64) NOT NULL,
    created_at_utc timestamptz NOT NULL,
    CONSTRAINT promotion_overlay_revision_catalog_key_shape
        CHECK (catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT promotion_overlay_revision_activation_positive
        CHECK (activation_revision >= 1),
    CONSTRAINT promotion_overlay_revision_digest_shape
        CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT promotion_overlay_revision_catalog_activation_unique
        UNIQUE (catalog_key, activation_revision)
);

CREATE INDEX promotion_overlay_revision_source_idx
    ON query.promotion_overlay_revision
    (catalog_key, source_public_read_revision_id, activation_revision DESC);

CREATE TABLE query.promotion_overlay_item
(
    overlay_id uuid NOT NULL
        REFERENCES query.promotion_overlay_revision(overlay_id) ON DELETE CASCADE,
    listing_id uuid NOT NULL,
    campaign_id uuid NOT NULL,
    position integer NOT NULL,
    locale varchar(35) NOT NULL,
    title varchar(300) NOT NULL,
    route_path varchar(500) NOT NULL,
    disclosure_label varchar(100) NOT NULL,
    PRIMARY KEY (overlay_id, position),
    CONSTRAINT promotion_overlay_item_listing_unique UNIQUE (overlay_id, listing_id),
    CONSTRAINT promotion_overlay_item_position_bounds CHECK (position BETWEEN 1 AND 100),
    CONSTRAINT promotion_overlay_item_listing_nonempty
        CHECK (listing_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT promotion_overlay_item_campaign_nonempty
        CHECK (campaign_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT promotion_overlay_item_route_shape
        CHECK (left(route_path, 1) = '/' AND position('..' IN route_path) = 0),
    CONSTRAINT promotion_overlay_item_text_nonempty
        CHECK
        (
            length(trim(locale)) > 0
            AND length(trim(title)) > 0
            AND length(trim(disclosure_label)) > 0
        )
);

CREATE TABLE query.current_promotion_overlay
(
    catalog_key varchar(96) PRIMARY KEY,
    overlay_id uuid NOT NULL
        REFERENCES query.promotion_overlay_revision(overlay_id),
    source_public_read_revision_id uuid NOT NULL,
    activation_revision bigint NOT NULL,
    activated_at_utc timestamptz NOT NULL,
    CONSTRAINT current_promotion_overlay_activation_positive
        CHECK (activation_revision >= 1)
);

CREATE TABLE query.promotion_overlay_checkpoint
(
    catalog_key varchar(96) PRIMARY KEY,
    activation_revision bigint NOT NULL,
    overlay_id uuid NOT NULL
        REFERENCES query.promotion_overlay_revision(overlay_id),
    updated_at_utc timestamptz NOT NULL,
    CONSTRAINT promotion_overlay_checkpoint_activation_positive
        CHECK (activation_revision >= 1)
);

CREATE TABLE query.promotion_overlay_inbox
(
    event_id uuid PRIMARY KEY,
    payload_digest char(64) NOT NULL,
    catalog_key varchar(96) NOT NULL,
    overlay_id uuid NOT NULL,
    source_public_read_revision_id uuid NOT NULL,
    activation_revision bigint NOT NULL,
    received_at_utc timestamptz NOT NULL,
    stale_ignored boolean NOT NULL,
    CONSTRAINT promotion_overlay_inbox_digest_shape
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT promotion_overlay_inbox_activation_positive
        CHECK (activation_revision >= 1)
);

CREATE INDEX promotion_overlay_inbox_catalog_activation_idx
    ON query.promotion_overlay_inbox
    (catalog_key, activation_revision DESC, received_at_utc DESC);
