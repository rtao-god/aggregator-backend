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

ALTER TABLE catalog.configuration_revision
    ADD COLUMN validation_contract_identity varchar(128) NOT NULL,
    ADD COLUMN validation_contract_revision integer NOT NULL,
    ADD COLUMN validation_state smallint NOT NULL,
    ADD COLUMN validation_result_digest char(64) NOT NULL,
    ADD COLUMN validated_at_utc timestamptz NOT NULL,
    ADD CONSTRAINT configuration_validation_contract_identity_valid CHECK
    (
        validation_contract_identity = 'aggregator-catalog-product-configuration-validation'
    ),
    ADD CONSTRAINT configuration_validation_contract_revision_valid CHECK
    (
        validation_contract_revision = 1
    ),
    ADD CONSTRAINT configuration_validation_state_valid CHECK
    (
        validation_state = 1
    ),
    ADD CONSTRAINT configuration_validation_result_digest_valid CHECK
    (
        validation_result_digest ~ '^[0-9a-f]{64}$'
    ),
    ADD CONSTRAINT configuration_validation_result_digest_unique UNIQUE
    (
        validation_result_digest
    );
