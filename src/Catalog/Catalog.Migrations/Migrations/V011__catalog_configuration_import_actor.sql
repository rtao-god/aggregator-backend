DO $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM catalog.configuration_revision
    )
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7111',
            MESSAGE = 'Catalog configuration import actor migration is blocked by existing revisions.',
            DETAIL = 'Existing configuration revisions do not contain a proven importing actor identity.',
            HINT = 'Export and re-import the exact authored configuration through the current Catalog owner contract before applying Catalog V011.';
    END IF;
END
$$;

CREATE TABLE catalog.configuration_import_actor
(
    configuration_revision_id uuid PRIMARY KEY
        REFERENCES catalog.configuration_revision(id)
        ON DELETE RESTRICT,
    imported_by_actor_id uuid NOT NULL,
    CONSTRAINT configuration_import_actor_nonempty CHECK
    (
        imported_by_actor_id <> '00000000-0000-0000-0000-000000000000'::uuid
    )
);
