DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM access_projection.listing_access_projection)
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Analytics Catalog access-grant migration is blocked by legacy listing access rows.',
            HINT = 'Clear the rebuildable Analytics listing access projection and replay the complete Catalog ListingAccessGrantChanged stream after applying V005.';
    END IF;
END
$$;

DROP TABLE access_projection.listing_access_projection;

CREATE TABLE access_projection.listing_access_grant_projection
(
    grant_id uuid PRIMARY KEY,
    listing_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    can_view_analytics boolean NOT NULL,
    granted_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NULL,
    revoked_at_utc timestamptz NULL,
    source_aggregate_revision bigint NOT NULL,
    source_payload_digest char(64) NOT NULL,
    projection_digest char(64) NOT NULL,
    changed_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_analytics_listing_access_grant_ids CHECK
    (
        grant_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND actor_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_listing_access_grant_revision CHECK
    (
        source_aggregate_revision > 0
    ),
    CONSTRAINT ck_analytics_listing_access_grant_digests CHECK
    (
        source_payload_digest ~ '^[0-9a-f]{64}$'
        AND projection_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT ck_analytics_listing_access_grant_interval CHECK
    (
        (expires_at_utc IS NULL OR expires_at_utc > granted_at_utc)
        AND changed_at_utc >= granted_at_utc
    ),
    CONSTRAINT ck_analytics_listing_access_grant_revocation CHECK
    (
        (
            revoked_at_utc IS NULL
            AND source_aggregate_revision = 1
        )
        OR
        (
            revoked_at_utc IS NOT NULL
            AND revoked_at_utc >= granted_at_utc
            AND source_aggregate_revision >= 2
            AND can_view_analytics = false
        )
    )
);

CREATE INDEX ix_analytics_listing_access_authorization
    ON access_projection.listing_access_grant_projection
       (actor_id, listing_id, can_view_analytics, revoked_at_utc, expires_at_utc);

CREATE TABLE messaging.listing_access_grant_inbox
(
    message_id uuid PRIMARY KEY,
    grant_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    routing_key varchar(200) NOT NULL,
    contract_identity varchar(200) NOT NULL,
    payload_digest char(64) NOT NULL,
    source_aggregate_revision bigint NOT NULL,
    received_at_utc timestamptz NOT NULL,
    correlation_id varchar(128) NOT NULL,
    causation_id uuid NULL,
    disposition integer NOT NULL,
    result_projection_digest char(64) NOT NULL,
    processed_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_analytics_access_grant_inbox_ids CHECK
    (
        message_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND grant_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND actor_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND (causation_id IS NULL OR causation_id <> '00000000-0000-0000-0000-000000000000'::uuid)
    ),
    CONSTRAINT ck_analytics_access_grant_inbox_metadata CHECK
    (
        length(btrim(routing_key)) > 0
        AND length(btrim(contract_identity)) > 0
        AND length(btrim(correlation_id)) > 0
    ),
    CONSTRAINT ck_analytics_access_grant_inbox_revision CHECK
    (
        source_aggregate_revision > 0
    ),
    CONSTRAINT ck_analytics_access_grant_inbox_digests CHECK
    (
        payload_digest ~ '^[0-9a-f]{64}$'
        AND result_projection_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT ck_analytics_access_grant_inbox_disposition CHECK
    (
        disposition BETWEEN 1 AND 3
    ),
    CONSTRAINT ck_analytics_access_grant_inbox_processing_time CHECK
    (
        processed_at_utc >= received_at_utc
    ),
    CONSTRAINT fk_analytics_access_grant_inbox_grant
        FOREIGN KEY (grant_id)
        REFERENCES access_projection.listing_access_grant_projection (grant_id)
        ON DELETE RESTRICT
);

CREATE INDEX ix_analytics_access_grant_inbox_grant_revision
    ON messaging.listing_access_grant_inbox
       (grant_id, source_aggregate_revision, message_id);
