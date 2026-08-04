CREATE SCHEMA projection;
CREATE SCHEMA documents;
CREATE SCHEMA messaging;

CREATE TABLE projection.base_projection
(
    id uuid PRIMARY KEY,
    catalog_key text NOT NULL,
    default_locale text NOT NULL,
    supported_locales text[] NOT NULL,
    source_publication_id uuid NOT NULL,
    source_publication_digest char(64) NOT NULL,
    source_publication_sequence bigint NOT NULL CHECK (source_publication_sequence > 0),
    builder_identity text NOT NULL,
    created_at_utc timestamptz NOT NULL,
    content_digest char(64) NOT NULL,
    CHECK (cardinality(supported_locales) > 0),
    CHECK (default_locale = ANY (supported_locales)),
    UNIQUE (catalog_key, source_publication_id, content_digest)
);

CREATE TABLE projection.overlay_revision
(
    id uuid PRIMARY KEY,
    catalog_key text NOT NULL,
    kind text NOT NULL CHECK (kind IN ('promotion', 'visibility_safety')),
    source_revision bigint NOT NULL CHECK (source_revision >= 0),
    created_at_utc timestamptz NOT NULL,
    content_digest char(64) NOT NULL,
    item_count integer NOT NULL CHECK (item_count >= 0),
    UNIQUE (catalog_key, kind, source_revision, content_digest)
);

CREATE TABLE projection.public_read_revision
(
    id uuid PRIMARY KEY,
    catalog_key text NOT NULL,
    base_projection_id uuid NOT NULL REFERENCES projection.base_projection (id),
    promotion_overlay_id uuid NOT NULL REFERENCES projection.overlay_revision (id),
    safety_overlay_id uuid NOT NULL REFERENCES projection.overlay_revision (id),
    source_publication_id uuid NOT NULL,
    created_at_utc timestamptz NOT NULL,
    content_digest char(64) NOT NULL,
    UNIQUE (catalog_key, base_projection_id, promotion_overlay_id, safety_overlay_id)
);

CREATE TABLE projection.current_public_read
(
    catalog_key text PRIMARY KEY,
    public_read_revision_id uuid NOT NULL UNIQUE REFERENCES projection.public_read_revision (id),
    activation_revision bigint NOT NULL CHECK (activation_revision > 0),
    activated_at_utc timestamptz NOT NULL
);

CREATE TABLE projection.catalog_activation_checkpoint
(
    catalog_key text PRIMARY KEY,
    last_activation_revision bigint NOT NULL CHECK (last_activation_revision > 0),
    current_public_read_revision_id uuid NOT NULL REFERENCES projection.public_read_revision (id),
    last_event_id uuid NOT NULL,
    last_payload_digest char(64) NOT NULL,
    updated_at_utc timestamptz NOT NULL
);

CREATE TABLE documents.listing_document
(
    base_projection_id uuid NOT NULL REFERENCES projection.base_projection (id),
    listing_id uuid NOT NULL,
    listing_revision_id uuid NOT NULL,
    subject_id uuid NOT NULL,
    subject_revision_id uuid NOT NULL,
    listing_kind text NOT NULL CHECK (listing_kind IN ('place', 'provider')),
    source_content_digest char(64) NOT NULL,
    published_at_utc timestamptz NOT NULL,
    PRIMARY KEY (base_projection_id, listing_id)
);

CREATE TABLE documents.listing_localization
(
    base_projection_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    locale text NOT NULL,
    route_path text NOT NULL,
    title text NOT NULL,
    description_state text NOT NULL CHECK (description_state IN ('observed', 'missing', 'not_applicable', 'withheld')),
    description text NULL,
    PRIMARY KEY (base_projection_id, listing_id, locale),
    UNIQUE (base_projection_id, route_path),
    FOREIGN KEY (base_projection_id, listing_id)
        REFERENCES documents.listing_document (base_projection_id, listing_id),
    CHECK (
        (description_state = 'observed' AND description IS NOT NULL AND length(btrim(description)) > 0)
        OR
        (description_state <> 'observed' AND description IS NULL)
    )
);

CREATE TABLE documents.listing_category
(
    base_projection_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    category_key text NOT NULL,
    PRIMARY KEY (base_projection_id, listing_id, category_key),
    FOREIGN KEY (base_projection_id, listing_id)
        REFERENCES documents.listing_document (base_projection_id, listing_id)
);

CREATE TABLE documents.listing_attribute
(
    base_projection_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    attribute_key text NOT NULL,
    state text NOT NULL CHECK (state IN ('observed', 'missing', 'not_applicable', 'withheld')),
    value_kind text NULL CHECK (value_kind IN ('boolean', 'decimal', 'text', 'text_collection', 'duration_minutes')),
    boolean_value boolean NULL,
    decimal_value numeric(19, 4) NULL,
    text_value text NULL,
    text_collection_value text[] NULL,
    PRIMARY KEY (base_projection_id, listing_id, attribute_key),
    FOREIGN KEY (base_projection_id, listing_id)
        REFERENCES documents.listing_document (base_projection_id, listing_id),
    CHECK (
        (
            state <> 'observed'
            AND value_kind IS NULL
            AND boolean_value IS NULL
            AND decimal_value IS NULL
            AND text_value IS NULL
            AND text_collection_value IS NULL
        )
        OR
        (
            state = 'observed'
            AND value_kind IS NOT NULL
            AND (
                (value_kind = 'boolean' AND boolean_value IS NOT NULL AND decimal_value IS NULL AND text_value IS NULL AND text_collection_value IS NULL)
                OR
                (value_kind IN ('decimal', 'duration_minutes') AND boolean_value IS NULL AND decimal_value IS NOT NULL AND text_value IS NULL AND text_collection_value IS NULL)
                OR
                (value_kind = 'text' AND boolean_value IS NULL AND decimal_value IS NULL AND text_value IS NOT NULL AND text_collection_value IS NULL)
                OR
                (value_kind = 'text_collection' AND boolean_value IS NULL AND decimal_value IS NULL AND text_value IS NULL AND cardinality(text_collection_value) > 0)
            )
        )
    )
);

CREATE TABLE documents.listing_geography
(
    base_projection_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    state text NOT NULL CHECK (state IN ('primary_market', 'nearby_market', 'remote_only', 'outside_market')),
    latitude numeric(9, 6) NULL CHECK (latitude BETWEEN -90 AND 90),
    longitude numeric(9, 6) NULL CHECK (longitude BETWEEN -180 AND 180),
    district_key text NULL,
    PRIMARY KEY (base_projection_id, listing_id),
    FOREIGN KEY (base_projection_id, listing_id)
        REFERENCES documents.listing_document (base_projection_id, listing_id),
    CHECK ((latitude IS NULL) = (longitude IS NULL)),
    CHECK (state = 'remote_only' OR (latitude IS NOT NULL AND longitude IS NOT NULL))
);

CREATE TABLE documents.listing_contact
(
    base_projection_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    kind text NOT NULL CHECK (kind IN ('website', 'email', 'phone', 'whatsapp', 'booking_reference', 'map_reference')),
    target text NOT NULL,
    label text NULL,
    PRIMARY KEY (base_projection_id, listing_id, ordinal),
    FOREIGN KEY (base_projection_id, listing_id)
        REFERENCES documents.listing_document (base_projection_id, listing_id)
);

CREATE TABLE documents.listing_media
(
    base_projection_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    media_id uuid NOT NULL,
    object_uri text NOT NULL,
    content_type text NOT NULL,
    content_digest char(64) NOT NULL,
    rights_basis text NOT NULL CHECK (rights_basis IN ('owner_provided', 'explicit_license', 'original_editorial_work', 'public_domain')),
    PRIMARY KEY (base_projection_id, listing_id, media_id),
    FOREIGN KEY (base_projection_id, listing_id)
        REFERENCES documents.listing_document (base_projection_id, listing_id)
);

CREATE TABLE documents.category_facet
(
    base_projection_id uuid NOT NULL REFERENCES projection.base_projection (id),
    category_key text NOT NULL,
    listing_count integer NOT NULL CHECK (listing_count >= 0),
    PRIMARY KEY (base_projection_id, category_key)
);

CREATE TABLE messaging.inbox_message
(
    event_id uuid PRIMARY KEY,
    event_type text NOT NULL,
    payload_digest char(64) NOT NULL,
    catalog_key text NOT NULL,
    activation_revision bigint NOT NULL CHECK (activation_revision > 0),
    outcome text NOT NULL CHECK (outcome IN ('activated', 'ignored_stale')),
    result_public_read_revision_id uuid NOT NULL REFERENCES projection.public_read_revision (id),
    received_at_utc timestamptz NOT NULL,
    UNIQUE (catalog_key, activation_revision)
);

CREATE INDEX ix_listing_document_projection_listing
    ON documents.listing_document (base_projection_id, listing_id);

CREATE INDEX ix_listing_category_projection_category_listing
    ON documents.listing_category (base_projection_id, category_key, listing_id);

CREATE INDEX ix_listing_localization_projection_locale_title
    ON documents.listing_localization (base_projection_id, locale, title);

CREATE INDEX ix_inbox_catalog_activation
    ON messaging.inbox_message (catalog_key, activation_revision);
