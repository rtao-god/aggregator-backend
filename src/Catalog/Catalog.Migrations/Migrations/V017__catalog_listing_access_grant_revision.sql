ALTER TABLE catalog.listing_access_grant
    ADD COLUMN aggregate_revision bigint NULL;

UPDATE catalog.listing_access_grant
SET aggregate_revision = CASE
    WHEN revoked_at_utc IS NULL THEN 1
    ELSE 2
END;

ALTER TABLE catalog.listing_access_grant
    ALTER COLUMN aggregate_revision SET NOT NULL,
    ADD CONSTRAINT listing_access_grant_revision_positive CHECK (aggregate_revision > 0),
    ADD CONSTRAINT listing_access_grant_revision_state_consistent CHECK (
        (revoked_at_utc IS NULL AND aggregate_revision = 1)
        OR
        (revoked_at_utc IS NOT NULL AND aggregate_revision >= 2));

ALTER TABLE catalog.listing_access_scope
    DROP CONSTRAINT listing_access_scope_valid,
    ADD CONSTRAINT listing_access_scope_valid CHECK (scope BETWEEN 1 AND 7);
