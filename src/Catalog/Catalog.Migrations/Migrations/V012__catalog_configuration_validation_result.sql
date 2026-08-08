DO $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM catalog.configuration_revision
    )
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7112',
            MESSAGE = 'Catalog configuration validation-result migration is blocked by existing revisions.',
            DETAIL = 'Existing configuration revisions do not contain a proven owner validation result.',
            HINT = 'Export and re-import the exact authored configuration through the current Catalog validation and import contract before applying Catalog V012.';
    END IF;
END
$$;

CREATE TABLE catalog.configuration_validation_result
(
    configuration_revision_id uuid PRIMARY KEY
        REFERENCES catalog.configuration_revision(id)
        ON DELETE RESTRICT,
    contract_identity varchar(128) NOT NULL,
    contract_revision integer NOT NULL,
    content_digest char(64) NOT NULL,
    validation_state smallint NOT NULL,
    result_digest char(64) NOT NULL UNIQUE,
    validated_at_utc timestamptz NOT NULL,
    CONSTRAINT configuration_validation_contract_identity_valid CHECK
    (
        contract_identity = 'aggregator-catalog-product-configuration-validation'
    ),
    CONSTRAINT configuration_validation_contract_revision_valid CHECK
    (
        contract_revision = 1
    ),
    CONSTRAINT configuration_validation_state_valid CHECK
    (
        validation_state = 1
    ),
    CONSTRAINT configuration_validation_content_digest_valid CHECK
    (
        content_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT configuration_validation_result_digest_valid CHECK
    (
        result_digest ~ '^[0-9a-f]{64}$'
    )
);

CREATE OR REPLACE FUNCTION catalog.ensure_configuration_validation_result()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM catalog.configuration_validation_result AS validation_result
        WHERE validation_result.configuration_revision_id = NEW.id
          AND validation_result.content_digest = NEW.content_digest
    )
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7113',
            MESSAGE = 'Catalog configuration revision has no matching owner validation result.',
            DETAIL = format(
                'Configuration revision %s with digest %s cannot be committed without its exact validation result.',
                NEW.id,
                NEW.content_digest),
            HINT = 'Persist the Catalog-owned validation result in the same transaction as the configuration revision.';
    END IF;

    RETURN NEW;
END
$$;

CREATE CONSTRAINT TRIGGER tr_catalog_configuration_validation_result_required
    AFTER INSERT OR UPDATE OF content_digest
    ON catalog.configuration_revision
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION catalog.ensure_configuration_validation_result();
