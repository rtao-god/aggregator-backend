CREATE TABLE catalog.listing_dispute
(
    id uuid PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES catalog.listing (id) ON DELETE RESTRICT,
    state integer NOT NULL,
    open_reason varchar(2000) NOT NULL,
    opened_by_actor_id uuid NOT NULL,
    opened_at_utc timestamptz NOT NULL,
    resolution_reason varchar(2000) NULL,
    resolved_by_actor_id uuid NULL,
    resolved_at_utc timestamptz NULL,
    aggregate_revision bigint NOT NULL,
    CONSTRAINT ck_catalog_listing_dispute_state CHECK (state IN (1, 2)),
    CONSTRAINT ck_catalog_listing_dispute_open_reason CHECK
    (
        length(btrim(open_reason)) BETWEEN 1 AND 2000
    ),
    CONSTRAINT ck_catalog_listing_dispute_revision CHECK (aggregate_revision > 0),
    CONSTRAINT ck_catalog_listing_dispute_lifecycle CHECK
    (
        (
            state = 1
            AND aggregate_revision = 1
            AND resolution_reason IS NULL
            AND resolved_by_actor_id IS NULL
            AND resolved_at_utc IS NULL
        )
        OR
        (
            state = 2
            AND aggregate_revision >= 2
            AND length(btrim(resolution_reason)) BETWEEN 1 AND 2000
            AND resolved_by_actor_id IS NOT NULL
            AND resolved_at_utc IS NOT NULL
            AND resolved_at_utc >= opened_at_utc
        )
    )
);

CREATE INDEX ix_catalog_listing_dispute_listing
    ON catalog.listing_dispute (listing_id, opened_at_utc DESC, id);

CREATE UNIQUE INDEX ux_catalog_listing_dispute_open
    ON catalog.listing_dispute (listing_id)
    WHERE state = 1;

CREATE OR REPLACE FUNCTION catalog.guard_listing_dispute_lifecycle()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7601',
            MESSAGE = 'Catalog listing dispute audit rows are immutable and cannot be deleted.';
    END IF;

    IF OLD.state = 2 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7601',
            MESSAGE = 'Resolved Catalog listing dispute rows are immutable.';
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id
       OR NEW.listing_id IS DISTINCT FROM OLD.listing_id
       OR NEW.open_reason IS DISTINCT FROM OLD.open_reason
       OR NEW.opened_by_actor_id IS DISTINCT FROM OLD.opened_by_actor_id
       OR NEW.opened_at_utc IS DISTINCT FROM OLD.opened_at_utc THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7601',
            MESSAGE = 'Catalog listing dispute opening evidence is immutable.';
    END IF;

    IF OLD.state <> 1
       OR NEW.state <> 2
       OR NEW.aggregate_revision <> OLD.aggregate_revision + 1
       OR NEW.resolution_reason IS NULL
       OR NEW.resolved_by_actor_id IS NULL
       OR NEW.resolved_at_utc IS NULL
       OR NEW.resolved_at_utc < OLD.opened_at_utc THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7601',
            MESSAGE = 'Catalog listing dispute transition must be one exact Open-to-Resolved revision.';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_catalog_listing_dispute_lifecycle
BEFORE UPDATE OR DELETE ON catalog.listing_dispute
FOR EACH ROW
EXECUTE FUNCTION catalog.guard_listing_dispute_lifecycle();

CREATE OR REPLACE FUNCTION catalog.block_disputed_publication_activation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    blocked_listing_id uuid;
    blocked_dispute_id uuid;
BEGIN
    SELECT entry.listing_id, dispute.id
    INTO blocked_listing_id, blocked_dispute_id
    FROM catalog.publication_entry AS entry
    INNER JOIN catalog.listing_dispute AS dispute
        ON dispute.listing_id = entry.listing_id
       AND dispute.state = 1
    WHERE entry.publication_id = NEW.publication_id
    ORDER BY entry.listing_id, dispute.id
    LIMIT 1;

    IF blocked_listing_id IS NOT NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7604',
            MESSAGE = format(
                'Catalog publication %s contains disputed listing %s.',
                NEW.publication_id,
                blocked_listing_id),
            DETAIL = format(
                'Open dispute %s blocks publication activation.',
                blocked_dispute_id),
            HINT = 'Resolve every open Catalog listing dispute before activating this publication.';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_catalog_current_publication_dispute_gate
BEFORE INSERT OR UPDATE OF publication_id ON catalog.current_catalog_publication
FOR EACH ROW
EXECUTE FUNCTION catalog.block_disputed_publication_activation();
