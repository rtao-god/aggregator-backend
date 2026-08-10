CREATE TABLE seo_projection.sitemap_revision
(
    catalog_key varchar(200) NOT NULL,
    public_read_revision_id uuid NOT NULL,
    content_digest char(64) NOT NULL,
    record_count integer NOT NULL,
    built_at_utc timestamptz NOT NULL,
    PRIMARY KEY (catalog_key, public_read_revision_id),
    CONSTRAINT ck_query_sitemap_revision_catalog_key
        CHECK (
            btrim(catalog_key) <> '' AND
            catalog_key = btrim(catalog_key) AND
            catalog_key !~ '[[:cntrl:]]'),
    CONSTRAINT ck_query_sitemap_revision_identity_nonempty
        CHECK (public_read_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_query_sitemap_revision_digest
        CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_query_sitemap_revision_record_count
        CHECK (record_count >= 0)
);

ALTER TABLE seo_projection.sitemap_record
    ADD CONSTRAINT fk_query_sitemap_record_revision
    FOREIGN KEY (catalog_key, public_read_revision_id)
    REFERENCES seo_projection.sitemap_revision(catalog_key, public_read_revision_id)
    DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE seo_projection.active_sitemap_revision
(
    catalog_key varchar(200) PRIMARY KEY,
    public_read_revision_id uuid NOT NULL,
    activated_at_utc timestamptz NOT NULL,
    CONSTRAINT fk_query_active_sitemap_revision
        FOREIGN KEY (catalog_key, public_read_revision_id)
        REFERENCES seo_projection.sitemap_revision(catalog_key, public_read_revision_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_query_active_sitemap_catalog_key
        CHECK (
            btrim(catalog_key) <> '' AND
            catalog_key = btrim(catalog_key) AND
            catalog_key !~ '[[:cntrl:]]'),
    CONSTRAINT ck_query_active_sitemap_revision_nonempty
        CHECK (public_read_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid)
);

CREATE INDEX ix_query_active_sitemap_revision_identity
    ON seo_projection.active_sitemap_revision(public_read_revision_id, catalog_key);

CREATE OR REPLACE FUNCTION seo_projection.verify_active_sitemap_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    expected_count integer;
    actual_count integer;
BEGIN
    SELECT revision.record_count
    INTO expected_count
    FROM seo_projection.sitemap_revision revision
    WHERE revision.catalog_key = NEW.catalog_key
      AND revision.public_read_revision_id = NEW.public_read_revision_id;

    IF expected_count IS NULL THEN
        RAISE EXCEPTION 'Query active sitemap pointer references a missing revision.'
            USING ERRCODE = 'P7610';
    END IF;

    SELECT count(*)::integer
    INTO actual_count
    FROM seo_projection.sitemap_record record
    WHERE record.catalog_key = NEW.catalog_key
      AND record.public_read_revision_id = NEW.public_read_revision_id;

    IF actual_count <> expected_count THEN
        RAISE EXCEPTION 'Query active sitemap pointer record count does not match its immutable revision.'
            USING ERRCODE = 'P7610';
    END IF;

    RETURN NEW;
END;
$$;

CREATE CONSTRAINT TRIGGER trg_query_active_sitemap_revision_verify
AFTER INSERT OR UPDATE ON seo_projection.active_sitemap_revision
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION seo_projection.verify_active_sitemap_revision();

CREATE OR REPLACE FUNCTION seo_projection.reject_sitemap_revision_change()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Query sitemap revision manifests are immutable.' USING ERRCODE = 'P7611';
END;
$$;

CREATE TRIGGER trg_query_sitemap_revision_immutable
BEFORE UPDATE OR DELETE ON seo_projection.sitemap_revision
FOR EACH ROW EXECUTE FUNCTION seo_projection.reject_sitemap_revision_change();

CREATE OR REPLACE FUNCTION seo_projection.reject_active_sitemap_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Query active sitemap pointer cannot be deleted.' USING ERRCODE = 'P7612';
END;
$$;

CREATE TRIGGER trg_query_active_sitemap_no_delete
BEFORE DELETE ON seo_projection.active_sitemap_revision
FOR EACH ROW EXECUTE FUNCTION seo_projection.reject_active_sitemap_delete();
