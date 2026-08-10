CREATE OR REPLACE FUNCTION seo_projection.verify_sitemap_record_self_link()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM seo_projection.sitemap_hreflang self_link
        WHERE self_link.public_read_revision_id = NEW.public_read_revision_id
          AND self_link.catalog_key = NEW.catalog_key
          AND self_link.source_locale = NEW.locale
          AND self_link.source_path = NEW.path
          AND self_link.alternate_locale = NEW.locale
          AND self_link.alternate_path = NEW.path
    ) THEN
        RAISE EXCEPTION 'Query sitemap record is missing its exact self hreflang link.'
            USING ERRCODE = 'P7609';
    END IF;

    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER trg_query_sitemap_record_self_link
AFTER INSERT ON seo_projection.sitemap_record
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION seo_projection.verify_sitemap_record_self_link();
