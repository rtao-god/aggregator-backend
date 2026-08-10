CREATE SCHEMA IF NOT EXISTS analytics_usage_projection;

CREATE TABLE analytics_usage_projection.inbox_message
(
    message_id uuid PRIMARY KEY,
    contract_identity varchar(300) NOT NULL,
    payload_digest char(64) NOT NULL,
    correlation_id varchar(200) NOT NULL,
    causation_id varchar(200),
    received_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_promotion_usage_inbox_contract_nonempty
        CHECK (btrim(contract_identity) <> '' AND contract_identity = btrim(contract_identity)),
    CONSTRAINT ck_promotion_usage_inbox_digest
        CHECK (payload_digest ~ '^[0-9A-Fa-f]{64}$'),
    CONSTRAINT ck_promotion_usage_inbox_correlation
        CHECK (btrim(correlation_id) <> '' AND correlation_id = btrim(correlation_id)),
    CONSTRAINT ck_promotion_usage_inbox_causation
        CHECK (causation_id IS NULL OR (btrim(causation_id) <> '' AND causation_id = btrim(causation_id)))
);

CREATE TABLE analytics_usage_projection.promotion_usage_window
(
    usage_window_id uuid PRIMARY KEY,
    placement_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    catalog_key varchar(200) NOT NULL,
    window_starts_at_utc timestamptz NOT NULL,
    window_ends_at_utc timestamptz NOT NULL,
    accepted_impressions bigint NOT NULL,
    accepted_listing_opens bigint NOT NULL,
    accepted_outbound_clicks bigint NOT NULL,
    aggregation_run_id uuid NOT NULL,
    source_aggregate_revision bigint NOT NULL,
    source_message_id uuid NOT NULL UNIQUE,
    source_payload_digest char(64) NOT NULL,
    source_occurred_at_utc timestamptz NOT NULL,
    applied_at_utc timestamptz NOT NULL,
    CONSTRAINT fk_promotion_usage_window_inbox
        FOREIGN KEY (source_message_id)
        REFERENCES analytics_usage_projection.inbox_message(message_id)
        ON DELETE RESTRICT,
    CONSTRAINT ux_promotion_usage_exact_window
        UNIQUE (placement_id, window_starts_at_utc, window_ends_at_utc),
    CONSTRAINT ck_promotion_usage_identities
        CHECK (
            usage_window_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
            placement_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
            listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
            aggregation_run_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_promotion_usage_catalog_key
        CHECK (btrim(catalog_key) <> '' AND catalog_key = btrim(catalog_key)),
    CONSTRAINT ck_promotion_usage_window
        CHECK (window_ends_at_utc > window_starts_at_utc AND window_ends_at_utc <= source_occurred_at_utc),
    CONSTRAINT ck_promotion_usage_counts
        CHECK (
            accepted_impressions >= 0 AND
            accepted_listing_opens >= 0 AND
            accepted_outbound_clicks >= 0 AND
            (accepted_impressions > 0 OR accepted_listing_opens > 0 OR accepted_outbound_clicks > 0)),
    CONSTRAINT ck_promotion_usage_revision
        CHECK (source_aggregate_revision > 0),
    CONSTRAINT ck_promotion_usage_digest
        CHECK (source_payload_digest ~ '^[0-9A-Fa-f]{64}$')
);

CREATE INDEX ix_promotion_usage_placement_window
    ON analytics_usage_projection.promotion_usage_window
    (placement_id, window_starts_at_utc, window_ends_at_utc);

CREATE INDEX ix_promotion_usage_listing_window
    ON analytics_usage_projection.promotion_usage_window
    (listing_id, window_starts_at_utc, window_ends_at_utc);

CREATE OR REPLACE FUNCTION analytics_usage_projection.reject_immutable_usage_change()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Analytics Promotion usage projection rows are immutable.' USING ERRCODE = 'P7605';
END;
$$;

CREATE TRIGGER trg_promotion_usage_inbox_immutable
BEFORE UPDATE OR DELETE ON analytics_usage_projection.inbox_message
FOR EACH ROW EXECUTE FUNCTION analytics_usage_projection.reject_immutable_usage_change();

CREATE TRIGGER trg_promotion_usage_window_immutable
BEFORE UPDATE OR DELETE ON analytics_usage_projection.promotion_usage_window
FOR EACH ROW EXECUTE FUNCTION analytics_usage_projection.reject_immutable_usage_change();
