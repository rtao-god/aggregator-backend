CREATE TABLE projection.promotion_placement_state
(
    placement_id uuid PRIMARY KEY,
    entitlement_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    catalog_key text NOT NULL,
    product_key text NOT NULL,
    scope_type text NOT NULL
        CHECK (scope_type IN ('catalog', 'category', 'district', 'editorial_landing')),
    scope_key text NOT NULL,
    locale_scope text[] NOT NULL,
    starts_at_utc timestamptz NOT NULL,
    ends_at_utc timestamptz NOT NULL,
    hard_expiry_at_utc timestamptz NOT NULL,
    priority_band integer NOT NULL CHECK (priority_band >= 0),
    capacity_slot integer NOT NULL CHECK (capacity_slot >= 0),
    presentation_label_key text NOT NULL,
    state text NOT NULL
        CHECK (state IN ('scheduled', 'active', 'paused', 'ended', 'revoked')),
    placement_revision bigint NOT NULL CHECK (placement_revision > 0),
    source_event_occurred_at_utc timestamptz NOT NULL,
    source_payload_digest char(64) NOT NULL
        CHECK (source_payload_digest ~ '^[0-9a-f]{64}$'),
    CHECK (cardinality(locale_scope) > 0),
    CHECK (starts_at_utc < hard_expiry_at_utc),
    CHECK (hard_expiry_at_utc <= ends_at_utc)
);

CREATE INDEX promotion_placement_state_catalog_materialization_idx
    ON projection.promotion_placement_state
    (catalog_key, state, priority_band DESC, capacity_slot, placement_id);

CREATE TABLE projection.promotion_overlay_item
(
    overlay_id uuid NOT NULL
        REFERENCES projection.overlay_revision(id),
    placement_id uuid NOT NULL,
    entitlement_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    product_key text NOT NULL,
    scope_type text NOT NULL
        CHECK (scope_type IN ('catalog', 'category', 'district', 'editorial_landing')),
    scope_key text NOT NULL,
    locale_scope text[] NOT NULL,
    starts_at_utc timestamptz NOT NULL,
    ends_at_utc timestamptz NOT NULL,
    hard_expiry_at_utc timestamptz NOT NULL,
    priority_band integer NOT NULL CHECK (priority_band >= 0),
    capacity_slot integer NOT NULL CHECK (capacity_slot >= 0),
    presentation_label_key text NOT NULL,
    placement_revision bigint NOT NULL CHECK (placement_revision > 0),
    PRIMARY KEY (overlay_id, placement_id),
    CHECK (cardinality(locale_scope) > 0),
    CHECK (starts_at_utc < hard_expiry_at_utc),
    CHECK (hard_expiry_at_utc <= ends_at_utc)
);

CREATE INDEX promotion_overlay_item_scope_idx
    ON projection.promotion_overlay_item
    (overlay_id, scope_type, scope_key, priority_band DESC, capacity_slot, placement_id);

CREATE INDEX promotion_overlay_item_listing_idx
    ON projection.promotion_overlay_item
    (overlay_id, listing_id);

CREATE TABLE messaging.promotion_inbox_message
(
    event_id uuid PRIMARY KEY,
    payload_digest char(64) NOT NULL
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    placement_id uuid NOT NULL,
    placement_revision bigint NOT NULL CHECK (placement_revision > 0),
    disposition text NOT NULL
        CHECK (disposition IN ('activated', 'replayed', 'ignored_stale')),
    result_public_read_revision_id uuid NOT NULL
        REFERENCES projection.public_read_revision(id),
    received_at_utc timestamptz NOT NULL
);

CREATE INDEX promotion_inbox_placement_revision_idx
    ON messaging.promotion_inbox_message
    (placement_id, placement_revision DESC, received_at_utc DESC);
