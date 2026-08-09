UPDATE processing.catalog_delivery
SET next_attempt_at_utc = GREATEST(
        COALESCE(next_attempt_at_utc, '-infinity'::timestamptz),
        last_changed_at_utc + INTERVAL '1 second',
        statement_timestamp() + INTERVAL '1 second'),
    failure_code = COALESCE(
        failure_code,
        'INGESTION_CATALOG_DELIVERY_RECOVERED_LEASE'),
    failure_detail = COALESCE(
        failure_detail,
        'A previously in-flight Catalog delivery was returned to the durable pending queue during the lease-safe owner migration.'),
    last_changed_at_utc = GREATEST(last_changed_at_utc, statement_timestamp())
WHERE state = 1
  AND attempt_count > 0
  AND (
      next_attempt_at_utc IS NULL
      OR next_attempt_at_utc <= last_changed_at_utc
      OR failure_code IS NULL
      OR failure_detail IS NULL
  );

ALTER TABLE processing.catalog_delivery
    ADD CONSTRAINT ck_ingestion_catalog_delivery_pending_shape CHECK
    (
        state <> 1
        OR
        (
            attempt_count = 0
            AND next_attempt_at_utc IS NULL
            AND failure_code IS NULL
            AND failure_detail IS NULL
        )
        OR
        (
            attempt_count > 0
            AND next_attempt_at_utc IS NOT NULL
            AND next_attempt_at_utc > last_changed_at_utc
            AND failure_code IS NOT NULL
            AND failure_detail IS NOT NULL
        )
    ),
    ADD CONSTRAINT ck_ingestion_catalog_delivery_lease_time CHECK
    (
        state <> 2
        OR lease_expires_at_utc > last_changed_at_utc
    ),
    ADD CONSTRAINT ck_ingestion_catalog_delivery_success_attempt CHECK
    (
        state <> 3
        OR attempt_count > 0
    );
