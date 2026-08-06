CREATE TABLE catalog.public_visibility_suppression
(
    id uuid PRIMARY KEY,
    catalog_key varchar(96) NOT NULL,
    target_kind integer NOT NULL,
    listing_id uuid NULL REFERENCES catalog.listing(id),
    target_key varchar(500) NOT NULL,
    public_reason_class varchar(96) NOT NULL,
    private_evidence_reference varchar(2048) NOT NULL,
    response_mode integer NOT NULL,
    starts_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NULL,
    state integer NOT NULL,
    revision bigint NOT NULL,
    changed_by_actor_id uuid NOT NULL,
    transition_reason varchar(4096) NOT NULL,
    changed_at_utc timestamptz NOT NULL,
    CONSTRAINT public_visibility_suppression_target_kind_valid
        CHECK (target_kind BETWEEN 1 AND 5),
    CONSTRAINT public_visibility_suppression_response_mode_valid
        CHECK (response_mode BETWEEN 1 AND 4),
    CONSTRAINT public_visibility_suppression_current_state_valid
        CHECK (state IN (2, 3)),
    CONSTRAINT public_visibility_suppression_current_revision_valid
        CHECK ((state = 2 AND revision = 2) OR (state = 3 AND revision = 3)),
    CONSTRAINT public_visibility_suppression_catalog_key_shape
        CHECK (catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT public_visibility_suppression_reason_shape
        CHECK (public_reason_class ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT public_visibility_suppression_target_scope_shape
        CHECK
        (
            (target_kind = 1 AND listing_id IS NOT NULL AND target_key = listing_id::text)
            OR
            (target_kind <> 1 AND listing_id IS NULL)
        ),
    CONSTRAINT public_visibility_suppression_uuid_target_shape
        CHECK
        (
            target_kind NOT IN (2, 3, 5)
            OR
            target_key ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        ),
    CONSTRAINT public_visibility_suppression_route_target_shape
        CHECK
        (
            target_kind <> 4
            OR
            (
                left(target_key, 1) = '/'
                AND position('..' IN target_key) = 0
                AND position('?' IN target_key) = 0
                AND position('#' IN target_key) = 0
            )
        ),
    CONSTRAINT public_visibility_suppression_response_target_shape
        CHECK
        (
            (target_kind IN (2, 3, 5) AND response_mode = 4)
            OR
            (target_kind IN (1, 4) AND response_mode IN (1, 2, 3))
        ),
    CONSTRAINT public_visibility_suppression_expiry_valid
        CHECK (expires_at_utc IS NULL OR expires_at_utc > starts_at_utc),
    CONSTRAINT public_visibility_suppression_change_time_valid
        CHECK (changed_at_utc >= starts_at_utc),
    CONSTRAINT public_visibility_suppression_actor_nonempty
        CHECK (changed_by_actor_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT public_visibility_suppression_text_nonempty
        CHECK
        (
            length(btrim(target_key)) > 0
            AND length(btrim(private_evidence_reference)) > 0
            AND length(btrim(transition_reason)) > 0
        )
);

CREATE UNIQUE INDEX public_visibility_suppression_active_target_unique
    ON catalog.public_visibility_suppression
    (catalog_key, target_kind, target_key)
    WHERE state = 2;

CREATE INDEX public_visibility_suppression_catalog_state_idx
    ON catalog.public_visibility_suppression
    (catalog_key, state, starts_at_utc, id);

CREATE TABLE catalog.public_visibility_suppression_revision
(
    suppression_id uuid NOT NULL
        REFERENCES catalog.public_visibility_suppression(id),
    revision bigint NOT NULL,
    catalog_key varchar(96) NOT NULL,
    target_kind integer NOT NULL,
    listing_id uuid NULL,
    target_key varchar(500) NOT NULL,
    public_reason_class varchar(96) NOT NULL,
    private_evidence_reference varchar(2048) NOT NULL,
    response_mode integer NOT NULL,
    starts_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NULL,
    state integer NOT NULL,
    changed_by_actor_id uuid NOT NULL,
    transition_reason varchar(4096) NOT NULL,
    changed_at_utc timestamptz NOT NULL,
    PRIMARY KEY (suppression_id, revision),
    CONSTRAINT public_visibility_suppression_revision_state_valid
        CHECK
        (
            (revision = 1 AND state = 1)
            OR
            (revision = 2 AND state = 2)
            OR
            (revision = 3 AND state = 3)
        ),
    CONSTRAINT public_visibility_suppression_revision_target_kind_valid
        CHECK (target_kind BETWEEN 1 AND 5),
    CONSTRAINT public_visibility_suppression_revision_response_mode_valid
        CHECK (response_mode BETWEEN 1 AND 4),
    CONSTRAINT public_visibility_suppression_revision_target_scope_shape
        CHECK
        (
            (target_kind = 1 AND listing_id IS NOT NULL AND target_key = listing_id::text)
            OR
            (target_kind <> 1 AND listing_id IS NULL)
        ),
    CONSTRAINT public_visibility_suppression_revision_response_target_shape
        CHECK
        (
            (target_kind IN (2, 3, 5) AND response_mode = 4)
            OR
            (target_kind IN (1, 4) AND response_mode IN (1, 2, 3))
        ),
    CONSTRAINT public_visibility_suppression_revision_expiry_valid
        CHECK (expires_at_utc IS NULL OR expires_at_utc > starts_at_utc),
    CONSTRAINT public_visibility_suppression_revision_change_time_valid
        CHECK (changed_at_utc >= starts_at_utc),
    CONSTRAINT public_visibility_suppression_revision_actor_nonempty
        CHECK (changed_by_actor_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT public_visibility_suppression_revision_text_nonempty
        CHECK
        (
            length(btrim(catalog_key)) > 0
            AND length(btrim(target_key)) > 0
            AND length(btrim(public_reason_class)) > 0
            AND length(btrim(private_evidence_reference)) > 0
            AND length(btrim(transition_reason)) > 0
        )
);

CREATE INDEX public_visibility_suppression_revision_catalog_idx
    ON catalog.public_visibility_suppression_revision
    (catalog_key, changed_at_utc, suppression_id, revision);
