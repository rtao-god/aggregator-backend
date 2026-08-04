CREATE SCHEMA IF NOT EXISTS analytics;

CREATE TABLE analytics.interaction_event
(
    event_id uuid PRIMARY KEY,
    request_digest char(64) NOT NULL,
    catalog_key varchar(96) NOT NULL,
    public_read_revision_id uuid NOT NULL,
    listing_id uuid NULL,
    session_hash char(64) NOT NULL,
    kind integer NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    recorded_at_utc timestamptz NOT NULL,
    CONSTRAINT interaction_event_request_digest_shape
        CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT interaction_event_session_hash_shape
        CHECK (session_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT interaction_event_catalog_key_shape
        CHECK (catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT interaction_event_kind_valid
        CHECK (kind BETWEEN 1 AND 4),
    CONSTRAINT interaction_event_listing_shape
        CHECK
        (
            (kind = 1 AND (listing_id IS NULL OR listing_id <> '00000000-0000-0000-0000-000000000000'::uuid))
            OR
            (kind BETWEEN 2 AND 4 AND listing_id IS NOT NULL AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        ),
    CONSTRAINT interaction_event_recording_order
        CHECK (recorded_at_utc >= occurred_at_utc - interval '1 hour')
);

CREATE INDEX interaction_event_catalog_recorded_idx
    ON analytics.interaction_event (catalog_key, recorded_at_utc DESC);
CREATE INDEX interaction_event_listing_recorded_idx
    ON analytics.interaction_event (catalog_key, listing_id, recorded_at_utc DESC)
    WHERE listing_id IS NOT NULL;
CREATE INDEX interaction_event_public_revision_idx
    ON analytics.interaction_event (public_read_revision_id);

CREATE TABLE analytics.listing_metric
(
    catalog_key varchar(96) NOT NULL,
    listing_id uuid NOT NULL,
    listing_views bigint NOT NULL DEFAULT 0,
    contact_clicks bigint NOT NULL DEFAULT 0,
    leads bigint NOT NULL DEFAULT 0,
    updated_at_utc timestamptz NOT NULL,
    PRIMARY KEY (catalog_key, listing_id),
    CONSTRAINT listing_metric_catalog_key_shape
        CHECK (catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT listing_metric_listing_id_nonempty
        CHECK (listing_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT listing_metric_counts_nonnegative
        CHECK (listing_views >= 0 AND contact_clicks >= 0 AND leads >= 0)
);

CREATE INDEX listing_metric_ranking_idx
    ON analytics.listing_metric
    (catalog_key, leads DESC, contact_clicks DESC, listing_views DESC, listing_id);
