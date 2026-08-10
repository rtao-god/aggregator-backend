CREATE TABLE aggregates.aggregate_run
(
    id uuid PRIMARY KEY,
    from_inclusive date NOT NULL,
    to_exclusive date NOT NULL,
    state integer NOT NULL,
    started_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz NULL,
    lease_token uuid NULL,
    lease_expires_at_utc timestamptz NULL,
    source_digest char(64) NULL,
    materialized_day_count integer NULL,
    materialized_metric_count integer NULL,
    removed_stale_metric_count integer NULL,
    failure_code varchar(160) NULL,
    failure_detail varchar(2000) NULL,
    required_action varchar(2000) NULL,
    CONSTRAINT ck_analytics_aggregate_run_id CHECK
    (
        id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_aggregate_run_range CHECK
    (
        to_exclusive > from_inclusive
        AND to_exclusive - from_inclusive <= 31
    ),
    CONSTRAINT ck_analytics_aggregate_run_state CHECK
    (
        state BETWEEN 1 AND 3
    ),
    CONSTRAINT ck_analytics_aggregate_run_shape CHECK
    (
        (
            state = 1
            AND completed_at_utc IS NULL
            AND lease_token IS NOT NULL
            AND lease_token <> '00000000-0000-0000-0000-000000000000'::uuid
            AND lease_expires_at_utc > started_at_utc
            AND source_digest IS NULL
            AND materialized_day_count IS NULL
            AND materialized_metric_count IS NULL
            AND removed_stale_metric_count IS NULL
            AND failure_code IS NULL
            AND failure_detail IS NULL
            AND required_action IS NULL
        )
        OR
        (
            state = 2
            AND completed_at_utc >= started_at_utc
            AND lease_token IS NULL
            AND lease_expires_at_utc IS NULL
            AND source_digest ~ '^[0-9a-f]{64}$'
            AND materialized_day_count = to_exclusive - from_inclusive
            AND materialized_metric_count >= 0
            AND removed_stale_metric_count >= 0
            AND failure_code IS NULL
            AND failure_detail IS NULL
            AND required_action IS NULL
        )
        OR
        (
            state = 3
            AND completed_at_utc >= started_at_utc
            AND lease_token IS NULL
            AND lease_expires_at_utc IS NULL
            AND source_digest IS NULL
            AND materialized_day_count IS NULL
            AND materialized_metric_count IS NULL
            AND removed_stale_metric_count IS NULL
            AND length(btrim(failure_code)) > 0
            AND length(btrim(failure_detail)) > 0
            AND length(btrim(required_action)) > 0
        )
    )
);

CREATE UNIQUE INDEX ux_analytics_aggregate_run_rebuilding
    ON aggregates.aggregate_run ((1))
    WHERE state = 1;
CREATE INDEX ix_analytics_aggregate_run_started_at_utc
    ON aggregates.aggregate_run (started_at_utc DESC, id DESC);
CREATE INDEX ix_analytics_aggregate_run_range
    ON aggregates.aggregate_run (from_inclusive, to_exclusive, started_at_utc DESC);

CREATE TABLE aggregates.aggregate_run_item
(
    run_id uuid NOT NULL,
    metric_date date NOT NULL,
    source_digest char(64) NOT NULL,
    metric_count integer NOT NULL,
    completed_at_utc timestamptz NOT NULL,
    PRIMARY KEY (run_id, metric_date),
    CONSTRAINT ck_analytics_aggregate_run_item_digest CHECK
    (
        source_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT ck_analytics_aggregate_run_item_count CHECK
    (
        metric_count >= 0
    ),
    CONSTRAINT fk_analytics_aggregate_run_item_run
        FOREIGN KEY (run_id)
        REFERENCES aggregates.aggregate_run (id)
        ON DELETE RESTRICT
);

CREATE TABLE aggregates.aggregate_readiness
(
    metric_date date PRIMARY KEY,
    run_id uuid NOT NULL,
    source_digest char(64) NOT NULL,
    metric_count integer NOT NULL,
    completed_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_analytics_aggregate_readiness_digest CHECK
    (
        source_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT ck_analytics_aggregate_readiness_count CHECK
    (
        metric_count >= 0
    ),
    CONSTRAINT fk_analytics_aggregate_readiness_run_item
        FOREIGN KEY (run_id, metric_date)
        REFERENCES aggregates.aggregate_run_item (run_id, metric_date)
        ON DELETE RESTRICT
);

CREATE OR REPLACE FUNCTION aggregates.guard_aggregate_run_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7605',
            MESSAGE = 'Analytics aggregate run history is immutable.',
            HINT = 'Create a new aggregation run instead of deleting owner evidence.';
    END IF;

    IF OLD.id <> NEW.id
       OR OLD.from_inclusive <> NEW.from_inclusive
       OR OLD.to_exclusive <> NEW.to_exclusive
       OR OLD.started_at_utc <> NEW.started_at_utc
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7605',
            MESSAGE = 'Analytics aggregate run identity and range are immutable.',
            HINT = 'Create a new aggregation run for a different range.';
    END IF;

    IF OLD.state <> 1 OR NEW.state NOT IN (2, 3) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7605',
            MESSAGE = 'Analytics aggregate run permits only rebuilding to terminal transition.',
            HINT = 'Create a new aggregation run instead of mutating terminal evidence.';
    END IF;

    RETURN NEW;
END
$$;

CREATE TRIGGER trg_analytics_aggregate_run_guard
BEFORE UPDATE OR DELETE ON aggregates.aggregate_run
FOR EACH ROW
EXECUTE FUNCTION aggregates.guard_aggregate_run_mutation();

CREATE OR REPLACE FUNCTION aggregates.reject_aggregate_run_item_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = 'P7606',
        MESSAGE = 'Analytics aggregate run items are immutable.',
        HINT = 'Create a new aggregation run instead of mutating completed date evidence.';
END
$$;

CREATE TRIGGER trg_analytics_aggregate_run_item_immutable
BEFORE UPDATE OR DELETE ON aggregates.aggregate_run_item
FOR EACH ROW
EXECUTE FUNCTION aggregates.reject_aggregate_run_item_mutation();

CREATE OR REPLACE FUNCTION aggregates.reject_aggregate_readiness_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = 'P7607',
        MESSAGE = 'Analytics aggregate readiness cannot be deleted.',
        HINT = 'Advance readiness through a newer complete aggregation run.';
END
$$;

CREATE TRIGGER trg_analytics_aggregate_readiness_no_delete
BEFORE DELETE ON aggregates.aggregate_readiness
FOR EACH ROW
EXECUTE FUNCTION aggregates.reject_aggregate_readiness_delete();
