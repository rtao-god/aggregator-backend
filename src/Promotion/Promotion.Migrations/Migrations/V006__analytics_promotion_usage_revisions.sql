ALTER TABLE analytics_usage_projection.promotion_usage_window
    DROP CONSTRAINT ck_promotion_usage_counts;

ALTER TABLE analytics_usage_projection.promotion_usage_window
    ADD CONSTRAINT ck_promotion_usage_counts
    CHECK
    (
        accepted_impressions >= 0
        AND accepted_listing_opens >= 0
        AND accepted_outbound_clicks >= 0
    );

DROP TRIGGER trg_promotion_usage_window_immutable
    ON analytics_usage_projection.promotion_usage_window;

CREATE TABLE analytics_usage_projection.promotion_usage_window_revision
(
    usage_window_id uuid NOT NULL,
    source_aggregate_revision bigint NOT NULL,
    placement_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    catalog_key varchar(200) NOT NULL,
    window_starts_at_utc timestamptz NOT NULL,
    window_ends_at_utc timestamptz NOT NULL,
    accepted_impressions bigint NOT NULL,
    accepted_listing_opens bigint NOT NULL,
    accepted_outbound_clicks bigint NOT NULL,
    aggregation_run_id uuid NOT NULL,
    source_message_id uuid NOT NULL UNIQUE,
    source_payload_digest char(64) NOT NULL,
    source_occurred_at_utc timestamptz NOT NULL,
    applied_at_utc timestamptz NOT NULL,
    PRIMARY KEY (usage_window_id, source_aggregate_revision),
    CONSTRAINT fk_promotion_usage_revision_inbox
        FOREIGN KEY (source_message_id)
        REFERENCES analytics_usage_projection.inbox_message(message_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_promotion_usage_revision_identities
        CHECK
        (
            usage_window_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND placement_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND aggregation_run_id <> '00000000-0000-0000-0000-000000000000'::uuid
        ),
    CONSTRAINT ck_promotion_usage_revision_catalog_key
        CHECK (btrim(catalog_key) <> '' AND catalog_key = btrim(catalog_key)),
    CONSTRAINT ck_promotion_usage_revision_window
        CHECK
        (
            window_ends_at_utc > window_starts_at_utc
            AND window_ends_at_utc <= source_occurred_at_utc
        ),
    CONSTRAINT ck_promotion_usage_revision_counts
        CHECK
        (
            accepted_impressions >= 0
            AND accepted_listing_opens >= 0
            AND accepted_outbound_clicks >= 0
        ),
    CONSTRAINT ck_promotion_usage_revision_number
        CHECK (source_aggregate_revision > 0),
    CONSTRAINT ck_promotion_usage_revision_digest
        CHECK (source_payload_digest ~ '^[0-9A-Fa-f]{64}$')
);

INSERT INTO analytics_usage_projection.promotion_usage_window_revision
(
    usage_window_id,
    source_aggregate_revision,
    placement_id,
    listing_id,
    catalog_key,
    window_starts_at_utc,
    window_ends_at_utc,
    accepted_impressions,
    accepted_listing_opens,
    accepted_outbound_clicks,
    aggregation_run_id,
    source_message_id,
    source_payload_digest,
    source_occurred_at_utc,
    applied_at_utc
)
SELECT usage_window_id,
       source_aggregate_revision,
       placement_id,
       listing_id,
       catalog_key,
       window_starts_at_utc,
       window_ends_at_utc,
       accepted_impressions,
       accepted_listing_opens,
       accepted_outbound_clicks,
       aggregation_run_id,
       source_message_id,
       source_payload_digest,
       source_occurred_at_utc,
       applied_at_utc
FROM analytics_usage_projection.promotion_usage_window;

CREATE INDEX ix_promotion_usage_revision_window
    ON analytics_usage_projection.promotion_usage_window_revision
    (placement_id, window_starts_at_utc, window_ends_at_utc, source_aggregate_revision DESC);

CREATE OR REPLACE FUNCTION analytics_usage_projection.guard_usage_window_identity()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION
            'Current Analytics Promotion usage windows cannot be deleted.'
            USING ERRCODE = 'P7605';
    END IF;

    IF OLD.usage_window_id <> NEW.usage_window_id
       OR OLD.placement_id <> NEW.placement_id
       OR OLD.listing_id <> NEW.listing_id
       OR OLD.catalog_key <> NEW.catalog_key
       OR OLD.window_starts_at_utc <> NEW.window_starts_at_utc
       OR OLD.window_ends_at_utc <> NEW.window_ends_at_utc
    THEN
        RAISE EXCEPTION
            'Analytics Promotion usage window identity is immutable.'
            USING ERRCODE = 'P7605';
    END IF;

    IF NEW.source_aggregate_revision <> OLD.source_aggregate_revision + 1 THEN
        RAISE EXCEPTION
            'Analytics Promotion usage revisions must advance contiguously.'
            USING ERRCODE = 'P7605';
    END IF;

    IF NEW.source_occurred_at_utc < OLD.source_occurred_at_utc
       OR NEW.applied_at_utc < OLD.applied_at_utc
    THEN
        RAISE EXCEPTION
            'Analytics Promotion usage revision time cannot move backwards.'
            USING ERRCODE = 'P7605';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_promotion_usage_window_revision_guard
BEFORE UPDATE OR DELETE ON analytics_usage_projection.promotion_usage_window
FOR EACH ROW EXECUTE FUNCTION analytics_usage_projection.guard_usage_window_identity();

CREATE OR REPLACE FUNCTION analytics_usage_projection.guard_usage_revision_identity()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM analytics_usage_projection.promotion_usage_window_revision existing
        WHERE existing.usage_window_id = NEW.usage_window_id
          AND
          (
              existing.placement_id <> NEW.placement_id
              OR existing.listing_id <> NEW.listing_id
              OR existing.catalog_key <> NEW.catalog_key
              OR existing.window_starts_at_utc <> NEW.window_starts_at_utc
              OR existing.window_ends_at_utc <> NEW.window_ends_at_utc
          )
    ) THEN
        RAISE EXCEPTION
            'Analytics Promotion usage revision identity diverges from its stream.'
            USING ERRCODE = 'P7605';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_promotion_usage_revision_identity
BEFORE INSERT ON analytics_usage_projection.promotion_usage_window_revision
FOR EACH ROW EXECUTE FUNCTION analytics_usage_projection.guard_usage_revision_identity();

CREATE TRIGGER trg_promotion_usage_revision_immutable
BEFORE UPDATE OR DELETE ON analytics_usage_projection.promotion_usage_window_revision
FOR EACH ROW EXECUTE FUNCTION analytics_usage_projection.reject_immutable_usage_change();

CREATE OR REPLACE FUNCTION analytics_usage_projection.require_current_usage_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM analytics_usage_projection.promotion_usage_window_revision revision
        WHERE revision.usage_window_id = NEW.usage_window_id
          AND revision.source_aggregate_revision = NEW.source_aggregate_revision
          AND revision.placement_id = NEW.placement_id
          AND revision.listing_id = NEW.listing_id
          AND revision.catalog_key = NEW.catalog_key
          AND revision.window_starts_at_utc = NEW.window_starts_at_utc
          AND revision.window_ends_at_utc = NEW.window_ends_at_utc
          AND revision.accepted_impressions = NEW.accepted_impressions
          AND revision.accepted_listing_opens = NEW.accepted_listing_opens
          AND revision.accepted_outbound_clicks = NEW.accepted_outbound_clicks
          AND revision.aggregation_run_id = NEW.aggregation_run_id
          AND revision.source_message_id = NEW.source_message_id
          AND revision.source_payload_digest = NEW.source_payload_digest
          AND revision.source_occurred_at_utc = NEW.source_occurred_at_utc
          AND revision.applied_at_utc = NEW.applied_at_utc
    ) THEN
        RAISE EXCEPTION
            'Current Analytics Promotion usage state lacks exact immutable revision evidence.'
            USING ERRCODE = 'P7605';
    END IF;

    RETURN NEW;
END;
$$;

CREATE CONSTRAINT TRIGGER trg_promotion_usage_current_has_revision
AFTER INSERT OR UPDATE ON analytics_usage_projection.promotion_usage_window
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION analytics_usage_projection.require_current_usage_revision();
