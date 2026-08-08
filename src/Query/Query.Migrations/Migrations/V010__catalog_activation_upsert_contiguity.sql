CREATE OR REPLACE FUNCTION projection.ensure_catalog_activation_revision_contiguous()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    previous_revision bigint;
BEGIN
    IF TG_OP = 'UPDATE'
    THEN
        previous_revision := OLD.last_activation_revision;
    ELSE
        SELECT checkpoint.last_activation_revision
        INTO previous_revision
        FROM projection.catalog_activation_checkpoint AS checkpoint
        WHERE checkpoint.catalog_key = NEW.catalog_key;
    END IF;

    PERFORM projection.assert_catalog_activation_revision_contiguous
    (
        NEW.catalog_key,
        previous_revision,
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
