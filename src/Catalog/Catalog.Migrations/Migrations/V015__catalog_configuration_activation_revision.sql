ALTER TABLE catalog.active_configuration
    ADD COLUMN aggregate_revision bigint;

UPDATE catalog.active_configuration
SET aggregate_revision = 1;

ALTER TABLE catalog.active_configuration
    ALTER COLUMN aggregate_revision SET NOT NULL;

ALTER TABLE catalog.active_configuration
    ADD CONSTRAINT ck_active_configuration_aggregate_revision
        CHECK (aggregate_revision > 0);
