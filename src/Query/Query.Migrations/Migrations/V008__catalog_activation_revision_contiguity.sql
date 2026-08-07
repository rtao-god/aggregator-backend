CREATE OR REPLACE FUNCTION projection.assert_catalog_activation_revision_contiguous
(
    p_catalog_key text,
    p_previous_revision bigint,
    p_incoming_revision bigint
)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    expected_revision bigint;
BEGIN
    expected_revision := COALESCE(p_previous_revision + 1, 1);
    IF p_incoming_revision <> expected_revision
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7202',
            MESSAGE = 'Query Catalog activation revision gap detected.',
            DETAIL = format(
                'Catalog %s expected activation revision %s but received %s.',
                p_catalog_key,
                expected_revision,
                p_incoming_revision),
            HINT = 'Replay the missing Catalog activation revisions in order or rebuild the Query projection from an exact owner operation.';
    END IF;
END
$$;

CREATE OR REPLACE FUNCTION projection.ensure_catalog_activation_revision_contiguous()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM projection.assert_catalog_activation_revision_contiguous
    (
        NEW.catalog_key,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.last_activation_revision ELSE NULL END,
        NEW.last_activation_revision
    );
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS tr_query_catalog_activation_revision_contiguous
    ON projection.catalog_activation_checkpoint;
CREATE TRIGGER tr_query_catalog_activation_revision_contiguous
    BEFORE INSERT OR UPDATE OF last_activation_revision
    ON projection.catalog_activation_checkpoint
    FOR EACH ROW EXECUTE FUNCTION projection.ensure_catalog_activation_revision_contiguous();

DO $$
DECLARE
    invalid_checkpoint record;
BEGIN
    SELECT
        checkpoint.catalog_key,
        checkpoint.last_activation_revision,
        missing_revision.revision
    INTO invalid_checkpoint
    FROM projection.catalog_activation_checkpoint AS checkpoint
    CROSS JOIN LATERAL
    (
        SELECT expected.revision
        FROM generate_series(1, checkpoint.last_activation_revision) AS expected(revision)
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM messaging.inbox_message AS inbox
            WHERE inbox.catalog_key = checkpoint.catalog_key
              AND inbox.activation_revision = expected.revision
        )
        ORDER BY expected.revision
        LIMIT 1
    ) AS missing_revision
    LIMIT 1;

    IF FOUND
    THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7201',
            MESSAGE = 'Query activation checkpoint migration is blocked by a historical revision gap.',
            DETAIL = format(
                'Catalog %s checkpoint is %s but activation revision %s is absent from the durable inbox.',
                invalid_checkpoint.catalog_key,
                invalid_checkpoint.last_activation_revision,
                invalid_checkpoint.revision),
            HINT = 'Rebuild the Query database from exact Catalog publication events before applying Query V008.';
    END IF;
END
$$;
