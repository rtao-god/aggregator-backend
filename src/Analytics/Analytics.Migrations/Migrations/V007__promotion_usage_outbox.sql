CREATE TABLE messaging.outbox_message
(
    message_id uuid PRIMARY KEY,
    routing_key text NOT NULL,
    contract_identity text NOT NULL,
    payload_json text NOT NULL,
    payload_digest char(64) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    correlation_id varchar(128) NOT NULL,
    causation_id uuid NOT NULL,
    delivery_attempts integer NOT NULL DEFAULT 0,
    lease_token uuid NULL,
    leased_by varchar(200) NULL,
    lease_expires_at_utc timestamptz NULL,
    dispatched_at_utc timestamptz NULL,
    dead_lettered_at_utc timestamptz NULL,
    dead_letter_reason varchar(2000) NULL,
    last_error varchar(2000) NULL,
    CONSTRAINT analytics_outbox_message_id_nonempty CHECK
    (
        message_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT analytics_outbox_payload_json_valid CHECK (payload_json IS JSON OBJECT),
    CONSTRAINT analytics_outbox_payload_digest_valid CHECK
    (
        payload_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT analytics_outbox_correlation_present CHECK
    (
        length(btrim(correlation_id)) > 0
    ),
    CONSTRAINT analytics_outbox_routing_key_present CHECK
    (
        length(btrim(routing_key)) > 0
    ),
    CONSTRAINT analytics_outbox_contract_identity_present CHECK
    (
        length(btrim(contract_identity)) > 0
    ),
    CONSTRAINT analytics_outbox_causation_nonempty CHECK
    (
        causation_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT analytics_outbox_attempts_nonnegative CHECK
    (
        delivery_attempts >= 0
    ),
    CONSTRAINT analytics_outbox_lease_consistent CHECK
    (
        (
            lease_token IS NULL
            AND leased_by IS NULL
            AND lease_expires_at_utc IS NULL
        )
        OR
        (
            lease_token IS NOT NULL
            AND leased_by IS NOT NULL
            AND lease_expires_at_utc IS NOT NULL
        )
    ),
    CONSTRAINT analytics_outbox_terminal_state_exclusive CHECK
    (
        dispatched_at_utc IS NULL
        OR dead_lettered_at_utc IS NULL
    ),
    CONSTRAINT analytics_outbox_dead_letter_reason_consistent CHECK
    (
        (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NULL)
        OR
        (dead_lettered_at_utc IS NOT NULL AND length(btrim(dead_letter_reason)) > 0)
    )
);

CREATE INDEX analytics_outbox_dispatch_idx
    ON messaging.outbox_message (occurred_at_utc, message_id)
    WHERE dispatched_at_utc IS NULL
      AND dead_lettered_at_utc IS NULL;

CREATE INDEX analytics_outbox_lease_expiry_idx
    ON messaging.outbox_message (lease_expires_at_utc)
    WHERE dispatched_at_utc IS NULL
      AND dead_lettered_at_utc IS NULL
      AND lease_token IS NOT NULL;

CREATE TABLE aggregates.promotion_usage_window_revision
(
    usage_window_id uuid NOT NULL,
    aggregate_revision bigint NOT NULL,
    placement_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    catalog_key varchar(200) NOT NULL,
    window_starts_at_utc timestamptz NOT NULL,
    window_ends_at_utc timestamptz NOT NULL,
    accepted_impressions bigint NOT NULL,
    accepted_listing_opens bigint NOT NULL,
    accepted_outbound_clicks bigint NOT NULL,
    source_digest char(64) NOT NULL,
    aggregation_run_id uuid NOT NULL,
    source_event_id uuid NOT NULL UNIQUE,
    source_payload_digest char(64) NOT NULL,
    source_occurred_at_utc timestamptz NOT NULL,
    materialized_at_utc timestamptz NOT NULL,
    PRIMARY KEY (usage_window_id, aggregate_revision),
    CONSTRAINT fk_analytics_promotion_usage_revision_run
        FOREIGN KEY (aggregation_run_id)
        REFERENCES aggregates.aggregate_run(id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_analytics_promotion_usage_revision_outbox
        FOREIGN KEY (source_event_id)
        REFERENCES messaging.outbox_message(message_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_analytics_promotion_usage_revision_identities CHECK
    (
        usage_window_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND placement_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND aggregation_run_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND source_event_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_promotion_usage_revision_catalog CHECK
    (
        length(btrim(catalog_key)) > 0
        AND catalog_key = btrim(catalog_key)
    ),
    CONSTRAINT ck_analytics_promotion_usage_revision_window CHECK
    (
        window_ends_at_utc > window_starts_at_utc
        AND window_ends_at_utc <= source_occurred_at_utc
    ),
    CONSTRAINT ck_analytics_promotion_usage_revision_counts CHECK
    (
        accepted_impressions >= 0
        AND accepted_listing_opens >= 0
        AND accepted_outbound_clicks >= 0
    ),
    CONSTRAINT ck_analytics_promotion_usage_revision_number CHECK
    (
        aggregate_revision > 0
    ),
    CONSTRAINT ck_analytics_promotion_usage_revision_digests CHECK
    (
        source_digest ~ '^[0-9a-f]{64}$'
        AND source_payload_digest ~ '^[0-9a-f]{64}$'
    )
);

CREATE TABLE aggregates.promotion_usage_window
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
    source_digest char(64) NOT NULL,
    aggregate_revision bigint NOT NULL,
    aggregation_run_id uuid NOT NULL,
    source_event_id uuid NOT NULL UNIQUE,
    source_payload_digest char(64) NOT NULL,
    source_occurred_at_utc timestamptz NOT NULL,
    materialized_at_utc timestamptz NOT NULL,
    CONSTRAINT ux_analytics_promotion_usage_window
        UNIQUE (placement_id, window_starts_at_utc, window_ends_at_utc),
    CONSTRAINT fk_analytics_promotion_usage_run
        FOREIGN KEY (aggregation_run_id)
        REFERENCES aggregates.aggregate_run(id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_analytics_promotion_usage_outbox
        FOREIGN KEY (source_event_id)
        REFERENCES messaging.outbox_message(message_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_analytics_promotion_usage_identities CHECK
    (
        usage_window_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND placement_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND aggregation_run_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND source_event_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_promotion_usage_catalog CHECK
    (
        length(btrim(catalog_key)) > 0
        AND catalog_key = btrim(catalog_key)
    ),
    CONSTRAINT ck_analytics_promotion_usage_window CHECK
    (
        window_ends_at_utc > window_starts_at_utc
        AND window_ends_at_utc <= source_occurred_at_utc
    ),
    CONSTRAINT ck_analytics_promotion_usage_counts CHECK
    (
        accepted_impressions >= 0
        AND accepted_listing_opens >= 0
        AND accepted_outbound_clicks >= 0
    ),
    CONSTRAINT ck_analytics_promotion_usage_revision CHECK
    (
        aggregate_revision > 0
    ),
    CONSTRAINT ck_analytics_promotion_usage_digests CHECK
    (
        source_digest ~ '^[0-9a-f]{64}$'
        AND source_payload_digest ~ '^[0-9a-f]{64}$'
    )
);

CREATE INDEX ix_analytics_promotion_usage_window_range
    ON aggregates.promotion_usage_window
    (window_starts_at_utc, window_ends_at_utc, placement_id);

CREATE INDEX ix_analytics_promotion_usage_revision_window
    ON aggregates.promotion_usage_window_revision
    (placement_id, window_starts_at_utc, window_ends_at_utc, aggregate_revision DESC);

CREATE OR REPLACE FUNCTION aggregates.guard_promotion_usage_window_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7608',
            MESSAGE = 'Analytics Promotion usage current state cannot be deleted.',
            HINT = 'Publish an explicit zero-valued complete revision when accepted usage is corrected to zero.';
    END IF;

    IF OLD.usage_window_id <> NEW.usage_window_id
       OR OLD.placement_id <> NEW.placement_id
       OR OLD.listing_id <> NEW.listing_id
       OR OLD.catalog_key <> NEW.catalog_key
       OR OLD.window_starts_at_utc <> NEW.window_starts_at_utc
       OR OLD.window_ends_at_utc <> NEW.window_ends_at_utc
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7608',
            MESSAGE = 'Analytics Promotion usage window identity is immutable.',
            HINT = 'Create a different usage stream for a different placement-window identity.';
    END IF;

    IF NEW.aggregate_revision <> OLD.aggregate_revision + 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7608',
            MESSAGE = 'Analytics Promotion usage revisions must advance contiguously.',
            HINT = 'Rebuild or replay the missing exact Analytics usage revision.';
    END IF;

    IF NEW.source_occurred_at_utc < OLD.source_occurred_at_utc
       OR NEW.materialized_at_utc < OLD.materialized_at_utc
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7608',
            MESSAGE = 'Analytics Promotion usage revision time cannot move backwards.',
            HINT = 'Repair the exact aggregation-run ordering before materialization.';
    END IF;

    RETURN NEW;
END
$$;

CREATE TRIGGER trg_analytics_promotion_usage_window_guard
BEFORE UPDATE OR DELETE ON aggregates.promotion_usage_window
FOR EACH ROW EXECUTE FUNCTION aggregates.guard_promotion_usage_window_mutation();

CREATE OR REPLACE FUNCTION aggregates.reject_promotion_usage_revision_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = 'P7609',
        MESSAGE = 'Analytics Promotion usage revision history is immutable.',
        HINT = 'Create a new contiguous usage revision instead of mutating owner evidence.';
END
$$;

CREATE TRIGGER trg_analytics_promotion_usage_revision_immutable
BEFORE UPDATE OR DELETE ON aggregates.promotion_usage_window_revision
FOR EACH ROW EXECUTE FUNCTION aggregates.reject_promotion_usage_revision_mutation();

CREATE OR REPLACE FUNCTION aggregates.guard_promotion_usage_revision_identity()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM aggregates.promotion_usage_window_revision existing
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
        RAISE EXCEPTION USING
            ERRCODE = 'P7609',
            MESSAGE = 'Analytics Promotion usage revision identity diverges from its stream.',
            HINT = 'Keep placement, listing, Catalog, and UTC window identity stable across revisions.';
    END IF;

    RETURN NEW;
END
$$;

CREATE TRIGGER trg_analytics_promotion_usage_revision_identity
BEFORE INSERT ON aggregates.promotion_usage_window_revision
FOR EACH ROW EXECUTE FUNCTION aggregates.guard_promotion_usage_revision_identity();

CREATE OR REPLACE FUNCTION aggregates.require_current_promotion_usage_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM aggregates.promotion_usage_window_revision revision
        WHERE revision.usage_window_id = NEW.usage_window_id
          AND revision.aggregate_revision = NEW.aggregate_revision
          AND revision.placement_id = NEW.placement_id
          AND revision.listing_id = NEW.listing_id
          AND revision.catalog_key = NEW.catalog_key
          AND revision.window_starts_at_utc = NEW.window_starts_at_utc
          AND revision.window_ends_at_utc = NEW.window_ends_at_utc
          AND revision.accepted_impressions = NEW.accepted_impressions
          AND revision.accepted_listing_opens = NEW.accepted_listing_opens
          AND revision.accepted_outbound_clicks = NEW.accepted_outbound_clicks
          AND revision.source_digest = NEW.source_digest
          AND revision.aggregation_run_id = NEW.aggregation_run_id
          AND revision.source_event_id = NEW.source_event_id
          AND revision.source_payload_digest = NEW.source_payload_digest
          AND revision.source_occurred_at_utc = NEW.source_occurred_at_utc
          AND revision.materialized_at_utc = NEW.materialized_at_utc
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7609',
            MESSAGE = 'Analytics Promotion usage current state lacks exact immutable revision evidence.',
            HINT = 'Insert the exact revision and current state in one owner transaction.';
    END IF;

    RETURN NEW;
END
$$;

CREATE CONSTRAINT TRIGGER trg_analytics_promotion_usage_current_has_revision
AFTER INSERT OR UPDATE ON aggregates.promotion_usage_window
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION aggregates.require_current_promotion_usage_revision();
