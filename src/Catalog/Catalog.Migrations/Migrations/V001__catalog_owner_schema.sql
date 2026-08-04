CREATE SCHEMA IF NOT EXISTS catalog;

CREATE TABLE catalog.configuration_revision
(
    id uuid PRIMARY KEY,
    site_key varchar(96) NOT NULL,
    catalog_key varchar(96) NOT NULL,
    content_digest char(64) NOT NULL,
    canonical_document bytea NOT NULL,
    created_at_utc timestamptz NOT NULL,
    imported_at_utc timestamptz NOT NULL,
    CONSTRAINT configuration_revision_digest_shape CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT configuration_revision_catalog_digest_unique UNIQUE (catalog_key, content_digest)
);

CREATE TABLE catalog.active_configuration
(
    catalog_key varchar(96) PRIMARY KEY,
    configuration_revision_id uuid NOT NULL REFERENCES catalog.configuration_revision(id),
    activated_by_actor_id uuid NOT NULL,
    activated_at_utc timestamptz NOT NULL
);

CREATE TABLE catalog.listing
(
    id uuid PRIMARY KEY,
    catalog_key varchar(96) NOT NULL,
    subject_id uuid NOT NULL,
    subject_revision_id uuid NOT NULL,
    subject_kind integer NOT NULL,
    state integer NOT NULL,
    version bigint NOT NULL,
    latest_revision_number bigint NOT NULL,
    current_draft_revision_id uuid NULL,
    approved_revision_id uuid NULL,
    published_revision_id uuid NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    CONSTRAINT listing_catalog_subject_unique UNIQUE (catalog_key, subject_id),
    CONSTRAINT listing_subject_kind_valid CHECK (subject_kind IN (2, 3)),
    CONSTRAINT listing_state_valid CHECK (state BETWEEN 1 AND 4),
    CONSTRAINT listing_version_positive CHECK (version >= 1),
    CONSTRAINT listing_revision_number_nonnegative CHECK (latest_revision_number >= 0),
    CONSTRAINT listing_time_order CHECK (updated_at_utc >= created_at_utc)
);

CREATE INDEX listing_catalog_state_idx ON catalog.listing (catalog_key, state);

CREATE TABLE catalog.listing_revision
(
    id uuid PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES catalog.listing(id),
    revision_number bigint NOT NULL,
    configuration_revision_id uuid NOT NULL REFERENCES catalog.configuration_revision(id),
    subject_id uuid NOT NULL,
    subject_revision_id uuid NOT NULL,
    subject_kind integer NOT NULL,
    content_digest char(64) NOT NULL,
    created_by_actor_id uuid NOT NULL,
    created_at_utc timestamptz NOT NULL,
    CONSTRAINT listing_revision_number_positive CHECK (revision_number >= 1),
    CONSTRAINT listing_revision_subject_kind_valid CHECK (subject_kind IN (2, 3)),
    CONSTRAINT listing_revision_digest_shape CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT listing_revision_number_unique UNIQUE (listing_id, revision_number)
);

CREATE INDEX listing_revision_configuration_idx ON catalog.listing_revision (configuration_revision_id);

ALTER TABLE catalog.listing
    ADD CONSTRAINT listing_current_draft_revision_fk
        FOREIGN KEY (current_draft_revision_id) REFERENCES catalog.listing_revision(id),
    ADD CONSTRAINT listing_approved_revision_fk
        FOREIGN KEY (approved_revision_id) REFERENCES catalog.listing_revision(id),
    ADD CONSTRAINT listing_published_revision_fk
        FOREIGN KEY (published_revision_id) REFERENCES catalog.listing_revision(id);

CREATE TABLE catalog.provenance_assertion
(
    listing_revision_id uuid NOT NULL REFERENCES catalog.listing_revision(id) ON DELETE CASCADE,
    assertion_id uuid NOT NULL,
    source_kind integer NOT NULL,
    source_reference varchar(2048) NOT NULL,
    observed_at_utc timestamptz NOT NULL,
    recorded_at_utc timestamptz NOT NULL,
    usage_policy integer NOT NULL,
    evidence_digest char(64) NOT NULL,
    PRIMARY KEY (listing_revision_id, assertion_id),
    CONSTRAINT provenance_source_kind_valid CHECK (source_kind BETWEEN 1 AND 6),
    CONSTRAINT provenance_usage_policy_valid CHECK (usage_policy BETWEEN 1 AND 4),
    CONSTRAINT provenance_time_order CHECK (recorded_at_utc >= observed_at_utc),
    CONSTRAINT provenance_digest_shape CHECK (evidence_digest ~ '^[0-9a-f]{64}$')
);

CREATE TABLE catalog.localized_text
(
    listing_revision_id uuid NOT NULL REFERENCES catalog.listing_revision(id) ON DELETE CASCADE,
    field_kind varchar(24) NOT NULL,
    locale varchar(32) NOT NULL,
    state integer NOT NULL,
    text_value text NULL,
    assertion_id uuid NULL,
    missing_reason integer NULL,
    PRIMARY KEY (listing_revision_id, field_kind, locale),
    CONSTRAINT localized_text_field_kind_valid CHECK (field_kind IN ('name', 'description')),
    CONSTRAINT localized_text_state_valid CHECK (state IN (1, 2, 4)),
    CONSTRAINT localized_text_shape CHECK (
        (state = 1 AND text_value IS NOT NULL AND assertion_id IS NOT NULL AND missing_reason IS NULL)
        OR
        (state IN (2, 4) AND text_value IS NULL AND assertion_id IS NULL AND missing_reason BETWEEN 1 AND 5)
    ),
    CONSTRAINT localized_text_assertion_fk
        FOREIGN KEY (listing_revision_id, assertion_id)
        REFERENCES catalog.provenance_assertion(listing_revision_id, assertion_id)
);

CREATE TABLE catalog.category_assignment
(
    listing_revision_id uuid NOT NULL REFERENCES catalog.listing_revision(id) ON DELETE CASCADE,
    category_key varchar(96) NOT NULL,
    assertion_id uuid NOT NULL,
    PRIMARY KEY (listing_revision_id, category_key),
    CONSTRAINT category_assignment_assertion_fk
        FOREIGN KEY (listing_revision_id, assertion_id)
        REFERENCES catalog.provenance_assertion(listing_revision_id, assertion_id)
);

CREATE TABLE catalog.attribute_value
(
    listing_revision_id uuid NOT NULL REFERENCES catalog.listing_revision(id) ON DELETE CASCADE,
    attribute_key varchar(96) NOT NULL,
    state integer NOT NULL,
    value_kind integer NULL,
    boolean_value boolean NULL,
    decimal_value numeric(24, 8) NULL,
    text_value text NULL,
    text_set_value text[] NULL,
    assertion_id uuid NULL,
    missing_reason integer NULL,
    PRIMARY KEY (listing_revision_id, attribute_key),
    CONSTRAINT attribute_state_valid CHECK (state IN (1, 2, 3)),
    CONSTRAINT attribute_value_kind_valid CHECK (value_kind IS NULL OR value_kind BETWEEN 1 AND 5),
    CONSTRAINT attribute_observed_provenance CHECK (
        (state = 1 AND assertion_id IS NOT NULL AND missing_reason IS NULL AND value_kind IS NOT NULL)
        OR
        (state = 2 AND assertion_id IS NULL AND missing_reason BETWEEN 1 AND 5 AND value_kind IS NULL)
        OR
        (state = 3 AND assertion_id IS NULL AND missing_reason IS NULL AND value_kind IS NULL)
    ),
    CONSTRAINT attribute_typed_value_shape CHECK (
        (state <> 1 AND boolean_value IS NULL AND decimal_value IS NULL AND text_value IS NULL AND text_set_value IS NULL)
        OR
        (value_kind = 1 AND boolean_value IS NOT NULL AND decimal_value IS NULL AND text_value IS NULL AND text_set_value IS NULL)
        OR
        (value_kind IN (2, 5) AND boolean_value IS NULL AND decimal_value IS NOT NULL AND text_value IS NULL AND text_set_value IS NULL)
        OR
        (value_kind = 3 AND boolean_value IS NULL AND decimal_value IS NULL AND text_value IS NOT NULL AND text_set_value IS NULL)
        OR
        (value_kind = 4 AND boolean_value IS NULL AND decimal_value IS NULL AND text_value IS NULL AND cardinality(text_set_value) > 0)
    ),
    CONSTRAINT attribute_assertion_fk
        FOREIGN KEY (listing_revision_id, assertion_id)
        REFERENCES catalog.provenance_assertion(listing_revision_id, assertion_id)
);

CREATE TABLE catalog.geography
(
    listing_revision_id uuid PRIMARY KEY REFERENCES catalog.listing_revision(id) ON DELETE CASCADE,
    state integer NOT NULL,
    latitude numeric(9, 6) NULL,
    longitude numeric(9, 6) NULL,
    district_key varchar(96) NULL,
    assertion_id uuid NOT NULL,
    CONSTRAINT geography_state_valid CHECK (state BETWEEN 1 AND 5),
    CONSTRAINT geography_coordinate_pair CHECK ((latitude IS NULL) = (longitude IS NULL)),
    CONSTRAINT geography_latitude_valid CHECK (latitude IS NULL OR latitude BETWEEN -90 AND 90),
    CONSTRAINT geography_longitude_valid CHECK (longitude IS NULL OR longitude BETWEEN -180 AND 180),
    CONSTRAINT geography_remote_shape CHECK (state <> 3 OR (latitude IS NULL AND longitude IS NULL AND district_key IS NULL)),
    CONSTRAINT geography_assertion_fk
        FOREIGN KEY (listing_revision_id, assertion_id)
        REFERENCES catalog.provenance_assertion(listing_revision_id, assertion_id)
);

CREATE TABLE catalog.contact
(
    id uuid PRIMARY KEY,
    listing_revision_id uuid NOT NULL REFERENCES catalog.listing_revision(id) ON DELETE CASCADE,
    kind integer NOT NULL,
    target varchar(2048) NOT NULL,
    label varchar(256) NULL,
    assertion_id uuid NOT NULL,
    CONSTRAINT contact_kind_valid CHECK (kind BETWEEN 1 AND 6),
    CONSTRAINT contact_unique UNIQUE (listing_revision_id, kind, target),
    CONSTRAINT contact_assertion_fk
        FOREIGN KEY (listing_revision_id, assertion_id)
        REFERENCES catalog.provenance_assertion(listing_revision_id, assertion_id)
);

CREATE TABLE catalog.media
(
    media_id uuid NOT NULL,
    listing_revision_id uuid NOT NULL REFERENCES catalog.listing_revision(id) ON DELETE CASCADE,
    object_uri varchar(2048) NOT NULL,
    content_type varchar(256) NOT NULL,
    content_digest char(64) NOT NULL,
    rights_basis integer NOT NULL,
    rights_reference varchar(2048) NOT NULL,
    assertion_id uuid NOT NULL,
    PRIMARY KEY (listing_revision_id, media_id),
    CONSTRAINT media_digest_shape CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT media_rights_basis_valid CHECK (rights_basis BETWEEN 1 AND 4),
    CONSTRAINT media_assertion_fk
        FOREIGN KEY (listing_revision_id, assertion_id)
        REFERENCES catalog.provenance_assertion(listing_revision_id, assertion_id)
);

CREATE TABLE catalog.editorial_decision
(
    id uuid PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES catalog.listing(id),
    revision_id uuid NOT NULL REFERENCES catalog.listing_revision(id),
    kind integer NOT NULL,
    actor_id uuid NOT NULL,
    reason varchar(4096) NULL,
    decided_at_utc timestamptz NOT NULL,
    CONSTRAINT editorial_decision_kind_valid CHECK (kind IN (1, 2)),
    CONSTRAINT editorial_rejection_reason CHECK ((kind = 1 AND reason IS NULL) OR (kind = 2 AND length(trim(reason)) > 0))
);

CREATE INDEX editorial_decision_listing_revision_idx ON catalog.editorial_decision (listing_id, revision_id);

CREATE TABLE catalog.publication_sequence
(
    catalog_key varchar(96) PRIMARY KEY,
    next_sequence bigint NOT NULL,
    CONSTRAINT publication_sequence_positive CHECK (next_sequence >= 2)
);

CREATE TABLE catalog.publication
(
    id uuid PRIMARY KEY,
    catalog_key varchar(96) NOT NULL,
    configuration_revision_id uuid NOT NULL REFERENCES catalog.configuration_revision(id),
    sequence bigint NOT NULL,
    artifact_key varchar(1024) NOT NULL,
    artifact_digest char(64) NOT NULL,
    created_by_actor_id uuid NOT NULL,
    created_at_utc timestamptz NOT NULL,
    CONSTRAINT publication_sequence_value_positive CHECK (sequence >= 1),
    CONSTRAINT publication_digest_shape CHECK (artifact_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT publication_catalog_sequence_unique UNIQUE (catalog_key, sequence),
    CONSTRAINT publication_catalog_digest_unique UNIQUE (catalog_key, artifact_digest)
);

CREATE TABLE catalog.publication_entry
(
    publication_id uuid NOT NULL REFERENCES catalog.publication(id) ON DELETE CASCADE,
    listing_id uuid NOT NULL REFERENCES catalog.listing(id),
    listing_revision_id uuid NOT NULL REFERENCES catalog.listing_revision(id),
    subject_revision_id uuid NOT NULL,
    content_digest char(64) NOT NULL,
    PRIMARY KEY (publication_id, listing_id),
    CONSTRAINT publication_entry_digest_shape CHECK (content_digest ~ '^[0-9a-f]{64}$')
);

CREATE INDEX publication_entry_revision_idx ON catalog.publication_entry (listing_revision_id);

CREATE TABLE catalog.current_publication
(
    catalog_key varchar(96) PRIMARY KEY,
    publication_id uuid NOT NULL REFERENCES catalog.publication(id),
    publication_sequence bigint NOT NULL,
    activated_at_utc timestamptz NOT NULL,
    activated_by_actor_id uuid NOT NULL,
    CONSTRAINT current_publication_sequence_positive CHECK (publication_sequence >= 1)
);

CREATE TABLE catalog.listing_claim
(
    id uuid PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES catalog.listing(id),
    claimant_actor_id uuid NOT NULL,
    state integer NOT NULL,
    evidence_reference varchar(2048) NOT NULL,
    evidence_digest char(64) NOT NULL,
    submitted_at_utc timestamptz NOT NULL,
    decided_by_actor_id uuid NULL,
    decided_at_utc timestamptz NULL,
    decision_reason varchar(4096) NULL,
    CONSTRAINT listing_claim_state_valid CHECK (state BETWEEN 1 AND 4),
    CONSTRAINT listing_claim_digest_shape CHECK (evidence_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT listing_claim_decision_shape CHECK (
        (state = 1 AND decided_by_actor_id IS NULL AND decided_at_utc IS NULL AND decision_reason IS NULL)
        OR
        (state = 2 AND decided_by_actor_id IS NOT NULL AND decided_at_utc IS NOT NULL AND decision_reason IS NULL)
        OR
        (state IN (3, 4) AND decided_by_actor_id IS NOT NULL AND decided_at_utc IS NOT NULL AND length(trim(decision_reason)) > 0)
    )
);

CREATE INDEX listing_claim_lookup_idx ON catalog.listing_claim (listing_id, claimant_actor_id, state);
CREATE UNIQUE INDEX listing_claim_active_unique
    ON catalog.listing_claim (listing_id, claimant_actor_id)
    WHERE state IN (1, 2);

CREATE TABLE catalog.listing_access_grant
(
    id uuid PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES catalog.listing(id),
    actor_id uuid NOT NULL,
    granted_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NULL,
    claim_id uuid NOT NULL UNIQUE REFERENCES catalog.listing_claim(id),
    revoked_at_utc timestamptz NULL,
    revoked_by_actor_id uuid NULL,
    revocation_reason varchar(4096) NULL,
    CONSTRAINT listing_access_expiration_valid CHECK (expires_at_utc IS NULL OR expires_at_utc > granted_at_utc),
    CONSTRAINT listing_access_revocation_shape CHECK (
        (revoked_at_utc IS NULL AND revoked_by_actor_id IS NULL AND revocation_reason IS NULL)
        OR
        (revoked_at_utc IS NOT NULL AND revoked_by_actor_id IS NOT NULL AND length(trim(revocation_reason)) > 0)
    )
);

CREATE INDEX listing_access_grant_lookup_idx ON catalog.listing_access_grant (listing_id, actor_id);

CREATE TABLE catalog.listing_access_scope
(
    grant_id uuid NOT NULL REFERENCES catalog.listing_access_grant(id) ON DELETE CASCADE,
    scope integer NOT NULL,
    PRIMARY KEY (grant_id, scope),
    CONSTRAINT listing_access_scope_valid CHECK (scope BETWEEN 1 AND 4)
);

CREATE TABLE catalog.outbox_message
(
    id uuid PRIMARY KEY,
    event_type varchar(256) NOT NULL,
    event_revision integer NOT NULL,
    payload jsonb NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    published_at_utc timestamptz NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    last_error varchar(4096) NULL,
    CONSTRAINT outbox_event_revision_positive CHECK (event_revision >= 1),
    CONSTRAINT outbox_attempt_count_nonnegative CHECK (attempt_count >= 0)
);

CREATE INDEX outbox_pending_idx
    ON catalog.outbox_message (occurred_at_utc, id)
    WHERE published_at_utc IS NULL;
