CREATE OR REPLACE FUNCTION projection.guard_immutable_overlay_reinsert()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    existing projection.overlay_revision%ROWTYPE;
BEGIN
    SELECT *
    INTO existing
    FROM projection.overlay_revision
    WHERE id = NEW.id;

    IF NOT FOUND
    THEN
        RETURN NEW;
    END IF;

    IF existing.catalog_key = NEW.catalog_key
       AND existing.kind = NEW.kind
       AND existing.source_revision = NEW.source_revision
       AND existing.created_at_utc = NEW.created_at_utc
       AND existing.content_digest = NEW.content_digest
       AND existing.item_count = NEW.item_count
    THEN
        RETURN NULL;
    END IF;

    RAISE EXCEPTION USING
        ERRCODE = 'P7203',
        MESSAGE = 'Query immutable overlay identity was reused with different owner state.',
        DETAIL = format(
            'Overlay %s already exists for catalog %s with kind %s and source revision %s.',
            NEW.id,
            existing.catalog_key,
            existing.kind,
            existing.source_revision),
        HINT = 'Restore the exact immutable Query overlay or rebuild the projection from owner events.';
END
$$;

DROP TRIGGER IF EXISTS tr_query_immutable_overlay_reinsert_guard
    ON projection.overlay_revision;
CREATE TRIGGER tr_query_immutable_overlay_reinsert_guard
    BEFORE INSERT ON projection.overlay_revision
    FOR EACH ROW EXECUTE FUNCTION projection.guard_immutable_overlay_reinsert();
