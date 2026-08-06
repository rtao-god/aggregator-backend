CREATE TABLE projection.visibility_suppression_state
(
    suppression_id uuid PRIMARY KEY,
    catalog_key text NOT NULL,
    target_kind text NOT NULL
        CHECK (target_kind IN ('listing', 'media', 'contact', 'route', 'external_reference')),
    listing_id uuid NULL,
    target_key text NOT NULL,
    public_reason_class text NOT NULL,
    response_mode text NOT NULL
        CHECK (response_mode IN ('hide_as_not_found', 'gone', 'temporarily_unavailable', 'omit_child_element')),
    starts_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NULL,
    state text NOT NULL CHECK (state IN ('active', 'resolved')),
    aggregate_revision bigint NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    source_event_id uuid NOT NULL,
    source_payload_digest char(64) NOT NULL
        CHECK (source_payload_digest ~ '^[0-9a-f]{64}$'),
    CHECK
    (
        (state = 'active' AND aggregate_revision = 2)
        OR
        (state = 'resolved' AND aggregate_revision = 3)
    ),
    CHECK
    (
        (target_kind = 'listing' AND listing_id IS NOT NULL AND target_key = listing_id::text)
        OR
        (target_kind <> 'listing' AND listing_id IS NULL)
    ),
    CHECK
    (
        (target_kind IN ('media', 'contact', 'external_reference') AND response_mode = 'omit_child_element')
        OR
        (target_kind IN ('listing', 'route') AND response_mode <> 'omit_child_element')
    ),
    CHECK (expires_at_utc IS NULL OR expires_at_utc > starts_at_utc),
    CHECK (occurred_at_utc >= starts_at_utc)
);

CREATE INDEX visibility_suppression_state_catalog_active_idx
    ON projection.visibility_suppression_state
    (catalog_key, state, suppression_id);

CREATE TABLE projection.visibility_safety_overlay_item
(
    overlay_id uuid NOT NULL
        REFERENCES projection.overlay_revision(id),
    suppression_id uuid NOT NULL,
    target_kind text NOT NULL
        CHECK (target_kind IN ('listing', 'media', 'route')),
    listing_id uuid NULL,
    target_key text NOT NULL,
    public_reason_class text NOT NULL,
    response_mode text NOT NULL
        CHECK (response_mode IN ('hide_as_not_found', 'gone', 'temporarily_unavailable', 'omit_child_element')),
    starts_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NULL,
    aggregate_revision bigint NOT NULL CHECK (aggregate_revision = 2),
    occurred_at_utc timestamptz NOT NULL,
    PRIMARY KEY (overlay_id, suppression_id),
    CHECK
    (
        (target_kind = 'listing' AND listing_id IS NOT NULL AND target_key = listing_id::text)
        OR
        (target_kind <> 'listing' AND listing_id IS NULL)
    ),
    CHECK
    (
        (target_kind = 'media' AND response_mode = 'omit_child_element')
        OR
        (target_kind IN ('listing', 'route') AND response_mode <> 'omit_child_element')
    ),
    CHECK (expires_at_utc IS NULL OR expires_at_utc > starts_at_utc),
    CHECK (occurred_at_utc >= starts_at_utc)
);

CREATE INDEX visibility_safety_overlay_listing_idx
    ON projection.visibility_safety_overlay_item
    (overlay_id, listing_id)
    WHERE target_kind = 'listing';

CREATE INDEX visibility_safety_overlay_target_idx
    ON projection.visibility_safety_overlay_item
    (overlay_id, target_kind, target_key);

CREATE TABLE messaging.visibility_suppression_inbox_message
(
    event_id uuid PRIMARY KEY,
    payload_digest char(64) NOT NULL
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    catalog_key text NOT NULL,
    suppression_id uuid NOT NULL,
    suppression_revision bigint NOT NULL CHECK (suppression_revision IN (2, 3)),
    processing_state text NOT NULL
        CHECK (processing_state IN ('pending', 'completed', 'ignored_stale')),
    result_public_read_revision_id uuid NULL
        REFERENCES projection.public_read_revision(id),
    received_at_utc timestamptz NOT NULL,
    processed_at_utc timestamptz NULL,
    CHECK
    (
        (processing_state = 'pending' AND result_public_read_revision_id IS NULL AND processed_at_utc IS NULL)
        OR
        (processing_state <> 'pending' AND result_public_read_revision_id IS NOT NULL AND processed_at_utc IS NOT NULL)
    )
);

CREATE INDEX visibility_suppression_inbox_owner_revision_idx
    ON messaging.visibility_suppression_inbox_message
    (suppression_id, suppression_revision DESC, received_at_utc DESC);

CREATE TABLE projection.catalog_visibility_block
(
    block_id uuid PRIMARY KEY,
    catalog_key text NOT NULL,
    source_event_id uuid NOT NULL UNIQUE,
    suppression_id uuid NOT NULL,
    suppression_revision bigint NOT NULL CHECK (suppression_revision IN (2, 3)),
    payload_digest char(64) NOT NULL
        CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    reason_code text NOT NULL,
    blocked_at_utc timestamptz NOT NULL
);

CREATE INDEX catalog_visibility_block_catalog_idx
    ON projection.catalog_visibility_block
    (catalog_key, blocked_at_utc, block_id);
