DROP TRIGGER IF EXISTS trg_analytics_interaction_event_retention_guard
    ON events.interaction_event;

ALTER TABLE events.interaction_event
    ALTER COLUMN page_context DROP NOT NULL;

ALTER TABLE events.interaction_event_retention_audit
    ADD COLUMN had_page_context boolean;

UPDATE events.interaction_event_retention_audit
SET had_page_context = TRUE
WHERE had_page_context IS NULL;

ALTER TABLE events.interaction_event_retention_audit
    ALTER COLUMN had_page_context SET NOT NULL;

UPDATE events.interaction_event
SET page_context = NULL,
    placement_scope_key = NULL
WHERE retention_state = 2;

ALTER TABLE events.interaction_event
    DROP CONSTRAINT ck_analytics_interaction_event_retention_shape,
    ADD CONSTRAINT ck_analytics_interaction_event_retention_shape
    CHECK
    (
        (
            retention_state = 1
            AND page_context IS NOT NULL
            AND retained_at_utc IS NULL
            AND retention_operation_id IS NULL
        )
        OR
        (
            retention_state = 2
            AND page_context IS NULL
            AND placement_scope_key IS NULL
            AND retained_at_utc IS NOT NULL
            AND retention_operation_id IS NOT NULL
        )
    );

CREATE OR REPLACE FUNCTION events.guard_interaction_event_retention_transition()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.retention_state = 2 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7609',
            MESSAGE = 'A minimized Analytics interaction event cannot be mutated.',
            HINT = 'Use the immutable retention audit and retained aggregation receipt; deleted raw context cannot be restored in place.';
    END IF;

    IF OLD.retention_state <> 1
       OR NEW.retention_state <> 2
       OR NEW.page_context IS NOT NULL
       OR NEW.placement_scope_key IS NOT NULL
       OR NEW.retained_at_utc IS NULL
       OR NEW.retained_at_utc < OLD.received_at_utc
       OR NEW.retention_operation_id IS NULL
       OR OLD.id <> NEW.id
       OR OLD.client_event_id <> NEW.client_event_id
       OR OLD.event_kind <> NEW.event_kind
       OR OLD.catalog_key <> NEW.catalog_key
       OR OLD.listing_id IS DISTINCT FROM NEW.listing_id
       OR OLD.public_read_revision_id <> NEW.public_read_revision_id
       OR OLD.occurred_at_utc <> NEW.occurred_at_utc
       OR OLD.received_at_utc <> NEW.received_at_utc
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
            HINT = 'Delete only raw page, campaign and scope context while preserving the exact event and aggregate-driving receipt.';
    END IF;

    RETURN NEW;
END
$$;

CREATE TRIGGER trg_analytics_interaction_event_retention_guard
BEFORE UPDATE ON events.interaction_event
FOR EACH ROW
EXECUTE FUNCTION events.guard_interaction_event_retention_transition();
