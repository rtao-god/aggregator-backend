CREATE SCHEMA IF NOT EXISTS seo_projection;

CREATE TABLE seo_projection.sitemap_record
(
    public_read_revision_id uuid NOT NULL,
    route_kind smallint NOT NULL,
    catalog_key varchar(200) NOT NULL,
    locale varchar(5) NOT NULL,
    path varchar(2048) NOT NULL,
    canonical_path varchar(2048) NOT NULL,
    last_modified_at_utc timestamptz NOT NULL,
    PRIMARY KEY (public_read_revision_id, catalog_key, locale, path),
    CONSTRAINT ux_query_sitemap_revision_catalog_path
        UNIQUE (public_read_revision_id, catalog_key, path),
    CONSTRAINT ck_query_sitemap_revision_identity
        CHECK (public_read_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_query_sitemap_route_kind
        CHECK (route_kind IN (1, 2, 3)),
    CONSTRAINT ck_query_sitemap_catalog_key
        CHECK (
            btrim(catalog_key) <> '' AND
            catalog_key = btrim(catalog_key) AND
            catalog_key !~ '[[:cntrl:]]'),
    CONSTRAINT ck_query_sitemap_locale
        CHECK (locale ~ '^[a-z]{2}-[A-Z]{2}$'),
    CONSTRAINT ck_query_sitemap_self_canonical
        CHECK (path = canonical_path),
    CONSTRAINT ck_query_sitemap_indexable_path
        CHECK (
            left(path, 1) = '/' AND
            left(path, 2) <> '//' AND
            path NOT LIKE '%//%' AND
            position('?' IN path) = 0 AND
            position('#' IN path) = 0 AND
            path !~ '(^|/)\.\.?(/|$)' AND
            path !~ '[[:cntrl:]]' AND
            path = btrim(path))
);

CREATE TABLE seo_projection.sitemap_hreflang
(
    public_read_revision_id uuid NOT NULL,
    catalog_key varchar(200) NOT NULL,
    source_locale varchar(5) NOT NULL,
    source_path varchar(2048) NOT NULL,
    alternate_locale varchar(5) NOT NULL,
    alternate_path varchar(2048) NOT NULL,
    PRIMARY KEY
    (
        public_read_revision_id,
        catalog_key,
        source_locale,
        source_path,
        alternate_locale
    ),
    CONSTRAINT ux_query_sitemap_hreflang_source_target_path
        UNIQUE
        (
            public_read_revision_id,
            catalog_key,
            source_locale,
            source_path,
            alternate_path
        ),
    CONSTRAINT fk_query_sitemap_hreflang_source
        FOREIGN KEY
        (
            public_read_revision_id,
            catalog_key,
            source_locale,
            source_path
        )
        REFERENCES seo_projection.sitemap_record
        (
            public_read_revision_id,
            catalog_key,
            locale,
            path
        )
        DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT fk_query_sitemap_hreflang_target
        FOREIGN KEY
        (
            public_read_revision_id,
            catalog_key,
            alternate_locale,
            alternate_path
        )
        REFERENCES seo_projection.sitemap_record
        (
            public_read_revision_id,
            catalog_key,
            locale,
            path
        )
        DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT ck_query_sitemap_hreflang_locales
        CHECK (
            source_locale ~ '^[a-z]{2}-[A-Z]{2}$' AND
            alternate_locale ~ '^[a-z]{2}-[A-Z]{2}$'),
    CONSTRAINT ck_query_sitemap_hreflang_paths
        CHECK (
            left(source_path, 1) = '/' AND
            left(alternate_path, 1) = '/' AND
            position('?' IN source_path) = 0 AND
            position('#' IN source_path) = 0 AND
            position('?' IN alternate_path) = 0 AND
            position('#' IN alternate_path) = 0)
);

CREATE INDEX ix_query_sitemap_page
    ON seo_projection.sitemap_record
    (public_read_revision_id, catalog_key, locale, path);

CREATE INDEX ix_query_sitemap_hreflang_target
    ON seo_projection.sitemap_hreflang
    (public_read_revision_id, catalog_key, alternate_locale, alternate_path);

CREATE OR REPLACE FUNCTION seo_projection.reject_immutable_sitemap_change()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Query sitemap revision rows are immutable.' USING ERRCODE = 'P7606';
END;
$$;

CREATE TRIGGER trg_query_sitemap_record_immutable
BEFORE UPDATE OR DELETE ON seo_projection.sitemap_record
FOR EACH ROW EXECUTE FUNCTION seo_projection.reject_immutable_sitemap_change();

CREATE TRIGGER trg_query_sitemap_hreflang_immutable
BEFORE UPDATE OR DELETE ON seo_projection.sitemap_hreflang
FOR EACH ROW EXECUTE FUNCTION seo_projection.reject_immutable_sitemap_change();

CREATE OR REPLACE FUNCTION seo_projection.verify_sitemap_hreflang_group()
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
          AND self_link.source_locale = NEW.source_locale
          AND self_link.source_path = NEW.source_path
          AND self_link.alternate_locale = NEW.source_locale
          AND self_link.alternate_path = NEW.source_path
    ) THEN
        RAISE EXCEPTION 'Query sitemap route is missing its exact self hreflang link.'
            USING ERRCODE = 'P7607';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM seo_projection.sitemap_hreflang forward_link
        WHERE forward_link.public_read_revision_id = NEW.public_read_revision_id
          AND forward_link.catalog_key = NEW.catalog_key
          AND forward_link.source_locale = NEW.source_locale
          AND forward_link.source_path = NEW.source_path
          AND NOT EXISTS
          (
              SELECT 1
              FROM seo_projection.sitemap_hreflang reverse_link
              WHERE reverse_link.public_read_revision_id = forward_link.public_read_revision_id
                AND reverse_link.catalog_key = forward_link.catalog_key
                AND reverse_link.source_locale = forward_link.alternate_locale
                AND reverse_link.source_path = forward_link.alternate_path
                AND reverse_link.alternate_locale = forward_link.source_locale
                AND reverse_link.alternate_path = forward_link.source_path
          )
    ) THEN
        RAISE EXCEPTION 'Query sitemap hreflang group is not reciprocal.'
            USING ERRCODE = 'P7608';
    END IF;

    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER trg_query_sitemap_hreflang_group
AFTER INSERT ON seo_projection.sitemap_hreflang
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION seo_projection.verify_sitemap_hreflang_group();
