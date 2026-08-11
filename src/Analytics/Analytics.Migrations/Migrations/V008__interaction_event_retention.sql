CREATE SCHEMA IF NOT EXISTS operations;

ALTER TABLE events.interaction_event
    ADD COLUMN retention_state smallint NOT NULL DEFAULT 1,
    ADD COLUMN retained_at_utc timestamptz NULL,
    ADD COLUMN retention_operation_id uuid NULL;

ALTER TABLE events.interaction_event
    ADD CONSTRAINT ck_analytics_interaction_event_retention_state
    CHECK (retention_state IN (1, 2)),
    ADD CONSTRAINT ck_analytics_interaction_event_retention_shape
    CHECK
    (
        (retention_state = 1 AND retained_at_utc IS NULL AND retention_operation_id IS NULL)
        OR
        (retention_state = 2 AND retained_at_utc IS NOT NULL AND retention_operation_id IS NOT NULL)
    );

CREATE TABLE operations.interaction_event_retention_operation
(
    id uuid PRIMARY KEY,
    request_digest char(64) NOT NULL,
    retain_before_utc timestamptz NOT NULL,
    maximum_events integer NOT NULL,
    minimized_event_count integer NOT NULL,
    completed_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_analytics_retention_operation_id
        CHECK (id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_analytics_retention_operation_digest
        CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_analytics_retention_operation_batch
        CHECK (maximum_events BETWEEN 1 AND 5000),
    CONSTRAINT ck_analytics_retention_operation_count
        CHECK (minimized_event_count BETWEEN 0 AND maximum_events),
    CONSTRAINT ck_analytics_retention_operation_time
        CHECK (retain_before_utc < completed_at_utc)
);

CREATE TABLE events.interaction_event_retention_audit
(
    event_id uuid PRIMARY KEY,
    operation_id uuid NOT NULL,
    client_event_id uuid NOT NULL,
    event_kind integer NOT NULL,
    payload_digest char(64) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    campaign_parameter_count integer NOT NULL,
    had_placement_scope boolean NOT NULL,
    retained_at_utc timestamptz NOT NULL,
    CONSTRAINT fk_analytics_retention_audit_operation
        FOREIGN KEY (operation_id)
        REFERENCES operations.interaction_event_retention_operation(id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_analytics_retention_audit_event
        FOREIGN KEY (event_id)
        REFERENCES events.interaction_event(id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_analytics_retention_audit_ids
        CHECK
        (
            event_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND client_event_id <> '00000000-0000-0000-0000-000000000000'::uuid
        ),
    CONSTRAINT ck_analytics_retention_audit_event_kind
        CHECK (event_kind BETWEEN 1 AND 11),
    CONSTRAINT ck_analytics_retention_audit_digest
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_analytics_retention_audit_parameter_count
        CHECK (campaign_parameter_count >= 0)
);

ALTER TABLE events.interaction_event
    ADD CONSTRAINT fk_analytics_interaction_event_retention_operation
    FOREIGN KEY (retention_operation_id)
    REFERENCES operations.interaction_event_retention_operation(id)
    ON DELETE RESTRICT;

CREATE INDEX ix_analytics_interaction_event_retention_candidates
    ON events.interaction_event (occurred_at_utc, id)
    WHERE retention_state = 1;

CREATE INDEX ix_analytics_interaction_event_retention_operation
    ON events.interaction_event (retention_operation_id)
    WHERE retention_operation_id IS NOT NULL;

CREATE OR REPLACE FUNCTION operations.reject_retention_operation_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = 'P7608',
        MESSAGE = 'Analytics retention operation history is immutable.',
        HINT = 'Create a new retention operation instead of mutating completed evidence.';
END
$$;

CREATE TRIGGER trg_analytics_retention_operation_immutable
BEFORE UPDATE OR DELETE ON operations.interaction_event_retention_operation
FOR EACH ROW
EXECUTE FUNCTION operations.reject_retention_operation_mutation();

CREATE OR REPLACE FUNCTION events.guard_interaction_event_retention_transition()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.retention_state = 2 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7609',
            MESSAGE = 'A minimized Analytics interaction event cannot be mutated.',
            HINT = 'Use the immutable retention audit and aggregate evidence; raw context cannot be restored in place.';
    END IF;

    IF NEW.retention_state <> 2
       OR NEW.retained_at_utc IS NULL
       OR NEW.retention_operation_id IS NULL
       OR OLD.id <> NEW.id
       OR OLD.client_event_id <> NEW.client_event_id
       OR OLD.event_kind <> NEW.event_kind
       OR OLD.catalog_key <> NEW.catalog_key
       OR OLD.listing_id IS DISTINCT FROM NEW.listing_id
       OR OLD.public_read_revision_id <> NEW.public_read_revision_id
       OR OLD.occurred_at_utc <> NEW.occurred_at_utc
       OR OLD.received_at_utc <> NEW.received_at_utc
       OR OLD.page_context <> NEW.page_context
       OR OLD.placement_exposure_kind <> NEW.placement_exposure_kind
       OR OLD.placement_id IS DISTINCT FROM NEW.placement_id
       OR OLD.referrer_class <> NEW.referrer_class
       OR OLD.consent_mode <> NEW.consent_mode
       OR OLD.quality_state <> NEW.quality_state
       OR OLD.payload_digest <> NEW.payload_digest
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7609',
            MESSAGE = 'Analytics interaction events permit only the owner-defined raw-to-minimized retention transition.',
            HINT = 'Do not edit accepted event meaning during retention.';
    END IF;

    RETURN NEW;
END
$$;

CREATE TRIGGER trg_analytics_interaction_event_retention_guard
BEFORE UPDATE ON events.interaction_event
FOR EACH ROW
EXECUTE FUNCTION events.guard_interaction_event_retention_transition();

CREATE OR REPLACE FUNCTION events.reject_retention_audit_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = 'P7610',
        MESSAGE = 'Analytics interaction retention audit is immutable.',
        HINT = 'Create a new owner operation for later retention work.';
END
$$;

CREATE TRIGGER trg_analytics_retention_audit_immutable
BEFORE UPDATE OR DELETE ON events.interaction_event_retention_audit
FOR EACH ROW
EXECUTE FUNCTION events.reject_retention_audit_mutation();
