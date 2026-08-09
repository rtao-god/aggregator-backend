UPDATE processing.catalog_delivery
SET state = 1,
    worker_identity = NULL,
    lease_expires_at_utc = NULL,
    last_changed_at_utc = GREATEST(last_changed_at_utc, clock_timestamp())
WHERE state = 2;

ALTER TABLE processing.catalog_delivery
    ADD COLUMN lease_token uuid NULL,
    ADD COLUMN next_attempt_at_utc timestamp with time zone NULL,
    ADD COLUMN failure_detail text NULL;

UPDATE processing.catalog_delivery
SET failure_detail = concat('Catalog delivery failure: ', failure_code)
WHERE state = 4
  AND failure_code IS NOT NULL
  AND failure_detail IS NULL;

ALTER TABLE processing.catalog_delivery
    DROP CONSTRAINT ck_ingestion_catalog_delivery_lease,
    DROP CONSTRAINT ck_ingestion_catalog_delivery_outcome;

ALTER TABLE processing.catalog_delivery
    ADD CONSTRAINT ck_ingestion_catalog_delivery_lease_token CHECK
    (
        lease_token IS NULL
        OR lease_token <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    ADD CONSTRAINT ck_ingestion_catalog_delivery_lease CHECK
    (
        (
            state = 2
            AND worker_identity IS NOT NULL
            AND lease_token IS NOT NULL
            AND lease_expires_at_utc IS NOT NULL
        )
        OR
        (
            state <> 2
            AND worker_identity IS NULL
            AND lease_token IS NULL
            AND lease_expires_at_utc IS NULL
        )
    ),
    ADD CONSTRAINT ck_ingestion_catalog_delivery_retry CHECK
    (
        state = 1
        OR next_attempt_at_utc IS NULL
    ),
    ADD CONSTRAINT ck_ingestion_catalog_delivery_failure_tuple CHECK
    (
        (failure_code IS NULL AND failure_detail IS NULL)
        OR
        (
            failure_code IS NOT NULL
            AND failure_detail IS NOT NULL
            AND length(failure_code) BETWEEN 1 AND 200
            AND length(failure_detail) BETWEEN 1 AND 4000
        )
    ),
    ADD CONSTRAINT ck_ingestion_catalog_delivery_outcome CHECK
    (
        (
            state = 3
            AND catalog_listing_id IS NOT NULL
            AND catalog_listing_revision_id IS NOT NULL
            AND failure_code IS NULL
            AND failure_detail IS NULL
        )
        OR
        (
            state = 4
            AND catalog_listing_id IS NULL
            AND catalog_listing_revision_id IS NULL
            AND failure_code IS NOT NULL
            AND failure_detail IS NOT NULL
        )
        OR
        (
            state IN (1, 2)
            AND catalog_listing_id IS NULL
            AND catalog_listing_revision_id IS NULL
        )
    );

DROP INDEX processing.ix_ingestion_catalog_delivery_lease;

CREATE INDEX ix_ingestion_catalog_delivery_lease
    ON processing.catalog_delivery
    (
        state,
        next_attempt_at_utc,
        lease_expires_at_utc,
        created_at_utc,
        delivery_id
    );
