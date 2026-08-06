CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE SCHEMA IF NOT EXISTS products;
CREATE SCHEMA IF NOT EXISTS entitlements;
CREATE SCHEMA IF NOT EXISTS placements;
CREATE SCHEMA IF NOT EXISTS access_projection;
CREATE SCHEMA IF NOT EXISTS operations;
CREATE SCHEMA IF NOT EXISTS messaging;
CREATE SCHEMA IF NOT EXISTS audit;

CREATE TABLE products.promotion_product
(
    id uuid PRIMARY KEY,
    product_key varchar(120) NOT NULL,
    state smallint NOT NULL,
    current_revision_id uuid NOT NULL,
    aggregate_revision bigint NOT NULL,
    CONSTRAINT ux_promotion_product_key UNIQUE (product_key),
    CONSTRAINT ck_promotion_product_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_product_state CHECK (state BETWEEN 1 AND 3),
    CONSTRAINT ck_promotion_product_revision CHECK (aggregate_revision > 0)
);

CREATE TABLE products.promotion_product_revision
(
    id uuid PRIMARY KEY,
    product_id uuid NOT NULL,
    revision_number bigint NOT NULL,
    display_names_json jsonb NOT NULL,
    presentation_features_json jsonb NOT NULL,
    requires_verified_contact boolean NOT NULL,
    required_contact_capability varchar(120) NULL,
    created_by_actor_id uuid NOT NULL,
    created_at_utc timestamptz NOT NULL,
    content_digest char(64) NOT NULL,
    CONSTRAINT fk_promotion_product_revision_product
        FOREIGN KEY (product_id)
        REFERENCES products.promotion_product (id)
        ON DELETE RESTRICT,
    CONSTRAINT ux_promotion_product_revision_number UNIQUE (product_id, revision_number),
    CONSTRAINT ux_promotion_product_revision_digest UNIQUE (product_id, content_digest),
    CONSTRAINT ck_promotion_product_revision_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_product_revision_number CHECK (revision_number > 0),
    CONSTRAINT ck_promotion_product_revision_names CHECK (
        jsonb_typeof(display_names_json) = 'object' AND display_names_json <> '{}'::jsonb),
    CONSTRAINT ck_promotion_product_revision_features CHECK (
        jsonb_typeof(presentation_features_json) = 'array' AND jsonb_array_length(presentation_features_json) > 0),
    CONSTRAINT ck_promotion_product_contact_shape CHECK (
        required_contact_capability IS NULL OR requires_verified_contact),
    CONSTRAINT ck_promotion_product_revision_actor CHECK (
        created_by_actor_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_product_revision_digest CHECK (content_digest ~ '^[0-9a-f]{64}$')
);

ALTER TABLE products.promotion_product
    ADD CONSTRAINT fk_promotion_product_current_revision
    FOREIGN KEY (current_revision_id)
    REFERENCES products.promotion_product_revision (id)
    ON DELETE RESTRICT
    DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE entitlements.promotion_entitlement
(
    id uuid PRIMARY KEY,
    listing_id uuid NOT NULL,
    product_key varchar(120) NOT NULL,
    source_type smallint NOT NULL,
    external_reference varchar(500) NOT NULL,
    starts_at_utc timestamptz NOT NULL,
    ends_at_utc timestamptz NOT NULL,
    state smallint NOT NULL,
    created_by_actor_id uuid NOT NULL,
    audit_reason varchar(2000) NOT NULL,
    created_at_utc timestamptz NOT NULL,
    changed_at_utc timestamptz NOT NULL,
    aggregate_revision bigint NOT NULL,
    CONSTRAINT ck_promotion_entitlement_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_entitlement_listing CHECK (listing_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_entitlement_source CHECK (source_type BETWEEN 1 AND 3),
    CONSTRAINT ck_promotion_entitlement_state CHECK (state BETWEEN 1 AND 5),
    CONSTRAINT ck_promotion_entitlement_reference CHECK (length(btrim(external_reference)) > 0),
    CONSTRAINT ck_promotion_entitlement_window CHECK (ends_at_utc > starts_at_utc),
    CONSTRAINT ck_promotion_entitlement_actor CHECK (
        created_by_actor_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_entitlement_reason CHECK (length(btrim(audit_reason)) > 0),
    CONSTRAINT ck_promotion_entitlement_time CHECK (changed_at_utc >= created_at_utc),
    CONSTRAINT ck_promotion_entitlement_revision CHECK (aggregate_revision > 0)
);

CREATE INDEX ix_promotion_entitlement_listing
    ON entitlements.promotion_entitlement (listing_id, state, starts_at_utc, ends_at_utc);
CREATE INDEX ix_promotion_entitlement_product
    ON entitlements.promotion_entitlement (product_key);

CREATE TABLE placements.sponsored_placement
(
    id uuid PRIMARY KEY,
    entitlement_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    product_key varchar(120) NOT NULL,
    state smallint NOT NULL,
    current_revision_id uuid NOT NULL,
    changed_at_utc timestamptz NOT NULL,
    audit_reason varchar(2000) NOT NULL,
    aggregate_revision bigint NOT NULL,
    CONSTRAINT fk_sponsored_placement_entitlement
        FOREIGN KEY (entitlement_id)
        REFERENCES entitlements.promotion_entitlement (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_sponsored_placement_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_sponsored_placement_listing CHECK (listing_id <> '00000000-0000-7000-8000-000000000000'),
    CONSTRAINT ck_sponsored_placement_state CHECK (state BETWEEN 1 AND 5),
    CONSTRAINT ck_sponsored_placement_reason CHECK (length(btrim(audit_reason)) > 0),
    CONSTRAINT ck_sponsored_placement_revision CHECK (aggregate_revision > 0)
);

CREATE TABLE placements.sponsored_placement_revision
(
    id uuid PRIMARY KEY,
    placement_id uuid NOT NULL,
    revision_number bigint NOT NULL,
    catalog_key varchar(120) NOT NULL,
    scope_type smallint NOT NULL,
    scope_key varchar(120) NOT NULL,
    locale_scope_json jsonb NOT NULL,
    starts_at_utc timestamptz NOT NULL,
    ends_at_utc timestamptz NOT NULL,
    priority_band integer NOT NULL,
    capacity_slot integer NOT NULL,
    presentation_label_key varchar(120) NOT NULL,
    created_by_actor_id uuid NOT NULL,
    created_at_utc timestamptz NOT NULL,
    content_digest char(64) NOT NULL,
    CONSTRAINT fk_sponsored_placement_revision_placement
        FOREIGN KEY (placement_id)
        REFERENCES placements.sponsored_placement (id)
        ON DELETE RESTRICT,
    CONSTRAINT ux_sponsored_placement_revision_number UNIQUE (placement_id, revision_number),
    CONSTRAINT ux_sponsored_placement_revision_digest UNIQUE (placement_id, content_digest),
    CONSTRAINT ck_sponsored_placement_revision_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_sponsored_placement_revision_number CHECK (revision_number > 0),
    CONSTRAINT ck_sponsored_placement_catalog CHECK (length(btrim(catalog_key)) > 0),
    CONSTRAINT ck_sponsored_placement_scope CHECK (scope_type BETWEEN 1 AND 4),
    CONSTRAINT ck_sponsored_placement_scope_key CHECK (length(btrim(scope_key)) > 0),
    CONSTRAINT ck_sponsored_placement_locales CHECK (
        jsonb_typeof(locale_scope_json) = 'array' AND jsonb_array_length(locale_scope_json) > 0),
    CONSTRAINT ck_sponsored_placement_window CHECK (ends_at_utc > starts_at_utc),
    CONSTRAINT ck_sponsored_placement_priority CHECK (priority_band BETWEEN 0 AND 1000),
    CONSTRAINT ck_sponsored_placement_slot CHECK (capacity_slot BETWEEN 1 AND 1000),
    CONSTRAINT ck_sponsored_placement_label CHECK (length(btrim(presentation_label_key)) > 0),
    CONSTRAINT ck_sponsored_placement_actor CHECK (
        created_by_actor_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_sponsored_placement_digest CHECK (content_digest ~ '^[0-9a-f]{64}$')
);

ALTER TABLE placements.sponsored_placement
    ADD CONSTRAINT fk_sponsored_placement_current_revision
    FOREIGN KEY (current_revision_id)
    REFERENCES placements.sponsored_placement_revision (id)
    ON DELETE RESTRICT
    DEFERRABLE INITIALLY DEFERRED;

CREATE INDEX ix_sponsored_placement_entitlement
    ON placements.sponsored_placement (entitlement_id);
CREATE INDEX ix_sponsored_placement_listing
    ON placements.sponsored_placement (listing_id);
CREATE INDEX ix_sponsored_placement_revision_calendar
    ON placements.sponsored_placement_revision
       (catalog_key, starts_at_utc, ends_at_utc, capacity_slot);

CREATE TABLE placements.sponsored_placement_capacity
(
    placement_id uuid NOT NULL,
    placement_revision_id uuid NOT NULL,
    catalog_key varchar(120) NOT NULL,
    scope_type smallint NOT NULL,
    scope_key varchar(120) NOT NULL,
    locale varchar(35) NOT NULL,
    capacity_slot integer NOT NULL,
    starts_at_utc timestamptz NOT NULL,
    ends_at_utc timestamptz NOT NULL,
    placement_state smallint NOT NULL,
    PRIMARY KEY (placement_id, locale),
    CONSTRAINT fk_sponsored_capacity_placement
        FOREIGN KEY (placement_id)
        REFERENCES placements.sponsored_placement (id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_sponsored_capacity_revision
        FOREIGN KEY (placement_revision_id)
        REFERENCES placements.sponsored_placement_revision (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_sponsored_capacity_scope CHECK (scope_type BETWEEN 1 AND 4),
    CONSTRAINT ck_sponsored_capacity_scope_key CHECK (length(btrim(scope_key)) > 0),
    CONSTRAINT ck_sponsored_capacity_locale CHECK (length(btrim(locale)) > 0),
    CONSTRAINT ck_sponsored_capacity_slot CHECK (capacity_slot BETWEEN 1 AND 1000),
    CONSTRAINT ck_sponsored_capacity_window CHECK (ends_at_utc > starts_at_utc),
    CONSTRAINT ck_sponsored_capacity_state CHECK (placement_state IN (1, 2)),
    CONSTRAINT ex_sponsored_capacity_no_overlap
        EXCLUDE USING gist
        (
            catalog_key WITH =,
            scope_type WITH =,
            scope_key WITH =,
            locale WITH =,
            capacity_slot WITH =,
            tstzrange(starts_at_utc, ends_at_utc, '[)') WITH &&
        )
);

CREATE TABLE access_projection.listing_eligibility_projection
(
    catalog_key varchar(120) NOT NULL,
    listing_id uuid NOT NULL,
    is_published boolean NOT NULL,
    is_archived boolean NOT NULL,
    has_blocking_dispute boolean NOT NULL,
    has_verified_contact boolean NOT NULL,
    contact_capabilities_json jsonb NOT NULL,
    category_keys_json jsonb NOT NULL,
    district_key varchar(120) NULL,
    source_revision bigint NOT NULL,
    changed_at_utc timestamptz NOT NULL,
    PRIMARY KEY (catalog_key, listing_id),
    CONSTRAINT ck_promotion_eligibility_listing CHECK (
        listing_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_eligibility_state CHECK (NOT (is_archived AND is_published)),
    CONSTRAINT ck_promotion_eligibility_contacts CHECK (
        jsonb_typeof(contact_capabilities_json) = 'array'),
    CONSTRAINT ck_promotion_eligibility_categories CHECK (
        jsonb_typeof(category_keys_json) = 'array'),
    CONSTRAINT ck_promotion_eligibility_revision CHECK (source_revision > 0)
);

CREATE INDEX ix_promotion_eligibility_listing_revision
    ON access_projection.listing_eligibility_projection (listing_id, source_revision);

CREATE TABLE operations.command_result
(
    scope varchar(150) NOT NULL,
    idempotency_key varchar(200) NOT NULL,
    request_digest char(64) NOT NULL,
    result_kind varchar(50) NOT NULL,
    result_json jsonb NOT NULL,
    result_digest char(64) NOT NULL,
    actor_id uuid NOT NULL,
    correlation_id varchar(128) NOT NULL,
    created_at_utc timestamptz NOT NULL,
    PRIMARY KEY (scope, idempotency_key),
    CONSTRAINT ck_promotion_command_scope CHECK (length(btrim(scope)) > 0),
    CONSTRAINT ck_promotion_command_key CHECK (length(btrim(idempotency_key)) > 0),
    CONSTRAINT ck_promotion_command_request_digest CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_promotion_command_result_kind CHECK (
        result_kind IN ('product', 'entitlement', 'placement')),
    CONSTRAINT ck_promotion_command_result_digest CHECK (result_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_promotion_command_actor CHECK (
        actor_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_command_correlation CHECK (length(btrim(correlation_id)) > 0)
);

CREATE INDEX ix_promotion_command_created_at
    ON operations.command_result (created_at_utc);

CREATE TABLE messaging.outbox_message
(
    message_id uuid PRIMARY KEY,
    routing_key varchar(256) NOT NULL,
    contract_identity varchar(256) NOT NULL,
    payload_json jsonb NOT NULL,
    payload_digest char(64) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    correlation_id varchar(128) NOT NULL,
    causation_id uuid NULL,
    lease_token uuid NULL,
    leased_by varchar(200) NULL,
    lease_expires_at_utc timestamptz NULL,
    delivery_attempts integer NOT NULL DEFAULT 0,
    dispatched_at_utc timestamptz NULL,
    last_error varchar(2000) NULL,
    dead_lettered_at_utc timestamptz NULL,
    dead_letter_reason varchar(2000) NULL,
    CONSTRAINT ck_promotion_outbox_message_id CHECK (
        message_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_outbox_routing_key CHECK (length(btrim(routing_key)) > 0),
    CONSTRAINT ck_promotion_outbox_contract_identity CHECK (length(btrim(contract_identity)) > 0),
    CONSTRAINT ck_promotion_outbox_payload_digest CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_promotion_outbox_correlation CHECK (length(btrim(correlation_id)) > 0),
    CONSTRAINT ck_promotion_outbox_causation CHECK (
        causation_id IS NULL OR causation_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_outbox_attempts CHECK (delivery_attempts >= 0),
    CONSTRAINT ck_promotion_outbox_lease_shape CHECK
    (
        (lease_token IS NULL AND leased_by IS NULL AND lease_expires_at_utc IS NULL)
        OR
        (lease_token IS NOT NULL AND leased_by IS NOT NULL AND lease_expires_at_utc IS NOT NULL)
    ),
    CONSTRAINT ck_promotion_outbox_terminal_shape CHECK
    (
        NOT (dispatched_at_utc IS NOT NULL AND dead_lettered_at_utc IS NOT NULL)
    ),
    CONSTRAINT ck_promotion_outbox_dead_letter_shape CHECK
    (
        (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NULL)
        OR
        (dead_lettered_at_utc IS NOT NULL AND length(btrim(dead_letter_reason)) > 0)
    )
);

CREATE INDEX ix_promotion_outbox_pending
    ON messaging.outbox_message (occurred_at_utc, message_id)
    WHERE dispatched_at_utc IS NULL AND dead_lettered_at_utc IS NULL;
CREATE INDEX ix_promotion_outbox_lease_expiry
    ON messaging.outbox_message (lease_expires_at_utc)
    WHERE dispatched_at_utc IS NULL
      AND dead_lettered_at_utc IS NULL
      AND lease_expires_at_utc IS NOT NULL;

CREATE TABLE audit.audit_entry
(
    id uuid PRIMARY KEY,
    actor_id uuid NOT NULL,
    owner varchar(150) NOT NULL,
    action varchar(150) NOT NULL,
    resource_type varchar(100) NOT NULL,
    resource_id uuid NOT NULL,
    correlation_id varchar(128) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    detail_digest char(64) NOT NULL,
    CONSTRAINT ck_promotion_audit_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_audit_actor CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_audit_resource CHECK (resource_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_promotion_audit_owner CHECK (length(btrim(owner)) > 0),
    CONSTRAINT ck_promotion_audit_action CHECK (length(btrim(action)) > 0),
    CONSTRAINT ck_promotion_audit_resource_type CHECK (length(btrim(resource_type)) > 0),
    CONSTRAINT ck_promotion_audit_correlation CHECK (length(btrim(correlation_id)) > 0),
    CONSTRAINT ck_promotion_audit_digest CHECK (detail_digest ~ '^[0-9a-f]{64}$')
);

CREATE OR REPLACE FUNCTION operations.reject_immutable_promotion_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = '55000',
        MESSAGE = format('Immutable Promotion row in %.% cannot be %s.', TG_TABLE_SCHEMA, TG_TABLE_NAME, lower(TG_OP)),
        HINT = 'Create a new owner revision or command result instead of mutating immutable history.';
END;
$$;

CREATE TRIGGER tr_promotion_product_revision_immutable
    BEFORE UPDATE OR DELETE ON products.promotion_product_revision
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_promotion_mutation();

CREATE TRIGGER tr_sponsored_placement_revision_immutable
    BEFORE UPDATE OR DELETE ON placements.sponsored_placement_revision
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_promotion_mutation();

CREATE TRIGGER tr_promotion_command_result_immutable
    BEFORE UPDATE OR DELETE ON operations.command_result
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_promotion_mutation();
