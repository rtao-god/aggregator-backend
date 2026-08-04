DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'analytics'
          AND table_name = 'interaction_event')
       AND EXISTS (SELECT 1 FROM analytics.interaction_event)
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Analytics canonical-owner migration is blocked by legacy interaction events.',
            HINT = 'Export and explicitly transform legacy rows through an approved Analytics owner migration before applying V002.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'analytics'
          AND table_name = 'listing_metric')
       AND EXISTS (SELECT 1 FROM analytics.listing_metric)
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Analytics canonical-owner migration is blocked by legacy listing metrics.',
            HINT = 'Rebuild the affected aggregate range through the canonical Analytics aggregate owner before applying V002.';
    END IF;
END
$$;

DROP SCHEMA IF EXISTS analytics CASCADE;

CREATE SCHEMA events;
CREATE SCHEMA access_projection;
CREATE SCHEMA aggregates;

CREATE TABLE access_projection.public_read_reference
(
    public_read_revision_id uuid PRIMARY KEY,
    catalog_key varchar(100) NOT NULL,
    base_projection_id uuid NOT NULL,
    promotion_overlay_id uuid NOT NULL,
    safety_overlay_id uuid NOT NULL,
    source_publication_id uuid NOT NULL,
    public_read_content_digest char(64) NOT NULL,
    membership_digest char(64) NOT NULL,
    activated_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_analytics_public_read_ids CHECK
    (
        public_read_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND base_projection_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND promotion_overlay_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND safety_overlay_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND source_publication_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_public_read_catalog_key CHECK
    (
        catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'
    ),
    CONSTRAINT ck_analytics_public_read_content_digest CHECK
    (
        public_read_content_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT ck_analytics_public_read_membership_digest CHECK
    (
        membership_digest ~ '^[0-9a-f]{64}$'
    )
);

CREATE INDEX ix_public_read_reference_catalog_key_activated_at_utc
    ON access_projection.public_read_reference (catalog_key, activated_at_utc);

CREATE TABLE access_projection.public_listing_reference
(
    public_read_revision_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    PRIMARY KEY (public_read_revision_id, listing_id),
    CONSTRAINT ck_analytics_public_listing_id CHECK
    (
        listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT fk_analytics_public_listing_reference
        FOREIGN KEY (public_read_revision_id)
        REFERENCES access_projection.public_read_reference (public_read_revision_id)
        ON DELETE RESTRICT
);

CREATE INDEX ix_public_listing_reference_listing_id
    ON access_projection.public_listing_reference (listing_id);

CREATE TABLE access_projection.listing_access_projection
(
    listing_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    can_view_analytics boolean NOT NULL,
    source_aggregate_revision bigint NOT NULL,
    source_payload_digest char(64) NOT NULL,
    changed_at_utc timestamptz NOT NULL,
    PRIMARY KEY (listing_id, actor_id),
    CONSTRAINT ck_analytics_listing_access_ids CHECK
    (
        listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND actor_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_listing_access_revision CHECK
    (
        source_aggregate_revision > 0
    ),
    CONSTRAINT ck_analytics_listing_access_digest CHECK
    (
        source_payload_digest ~ '^[0-9a-f]{64}$'
    )
);

CREATE INDEX ix_listing_access_projection_actor_id_can_view_analytics
    ON access_projection.listing_access_projection (actor_id, can_view_analytics);

CREATE TABLE events.interaction_event
(
    id uuid PRIMARY KEY,
    client_event_id uuid NOT NULL,
    event_kind integer NOT NULL,
    catalog_key varchar(100) NOT NULL,
    listing_id uuid NULL,
    public_read_revision_id uuid NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    received_at_utc timestamptz NOT NULL,
    page_context varchar(120) NOT NULL,
    placement_exposure_kind integer NOT NULL,
    placement_id uuid NULL,
    placement_scope_key varchar(100) NULL,
    referrer_class integer NOT NULL,
    consent_mode integer NOT NULL,
    quality_state integer NOT NULL,
    payload_digest char(64) NOT NULL,
    CONSTRAINT ck_analytics_interaction_event_ids CHECK
    (
        id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND client_event_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND public_read_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND (listing_id IS NULL OR listing_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        AND (placement_id IS NULL OR placement_id <> '00000000-0000-0000-0000-000000000000'::uuid)
    ),
    CONSTRAINT ck_analytics_interaction_event_catalog_key CHECK
    (
        catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'
    ),
    CONSTRAINT ck_analytics_interaction_event_kind CHECK
    (
        event_kind BETWEEN 1 AND 11
    ),
    CONSTRAINT ck_analytics_interaction_event_listing_shape CHECK
    (
        (event_kind = 1 AND listing_id IS NULL)
        OR
        (event_kind BETWEEN 2 AND 11 AND listing_id IS NOT NULL)
    ),
    CONSTRAINT ck_analytics_interaction_event_placement_kind CHECK
    (
        placement_exposure_kind BETWEEN 1 AND 3
    ),
    CONSTRAINT ck_analytics_interaction_event_placement_shape CHECK
    (
        (placement_exposure_kind = 2 AND placement_id IS NOT NULL)
        OR
        (placement_exposure_kind <> 2 AND placement_id IS NULL)
    ),
    CONSTRAINT ck_analytics_interaction_event_context_enums CHECK
    (
        referrer_class BETWEEN 1 AND 7
        AND consent_mode BETWEEN 1 AND 2
        AND quality_state BETWEEN 1 AND 4
    ),
    CONSTRAINT ck_analytics_interaction_event_time_bounds CHECK
    (
        occurred_at_utc >= received_at_utc - INTERVAL '7 days'
        AND occurred_at_utc <= received_at_utc + INTERVAL '5 minutes'
    ),
    CONSTRAINT ck_analytics_interaction_event_digest CHECK
    (
        payload_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT fk_analytics_interaction_public_read_reference
        FOREIGN KEY (public_read_revision_id)
        REFERENCES access_projection.public_read_reference (public_read_revision_id)
        ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ux_analytics_interaction_event_semantic_key
    ON events.interaction_event (client_event_id, event_kind);
CREATE INDEX ix_interaction_event_catalog_key_listing_id_occurred_at_utc
    ON events.interaction_event (catalog_key, listing_id, occurred_at_utc);
CREATE INDEX ix_interaction_event_public_read_revision_id
    ON events.interaction_event (public_read_revision_id);

CREATE TABLE events.interaction_event_campaign_parameter
(
    event_id uuid NOT NULL,
    parameter_key varchar(32) NOT NULL,
    parameter_value varchar(200) NOT NULL,
    PRIMARY KEY (event_id, parameter_key),
    CONSTRAINT ck_analytics_campaign_parameter_key CHECK
    (
        parameter_key IN
        (
            'utm_source',
            'utm_medium',
            'utm_campaign',
            'utm_content',
            'utm_term'
        )
    ),
    CONSTRAINT ck_analytics_campaign_parameter_value CHECK
    (
        length(btrim(parameter_value)) > 0
    ),
    CONSTRAINT fk_analytics_campaign_parameter_event
        FOREIGN KEY (event_id)
        REFERENCES events.interaction_event (id)
        ON DELETE RESTRICT
);

CREATE TABLE aggregates.daily_listing_metric
(
    metric_date date NOT NULL,
    catalog_key varchar(100) NOT NULL,
    listing_id uuid NOT NULL,
    aggregation_source_digest char(64) NOT NULL,
    source_read_revision_count integer NOT NULL,
    readiness_state integer NOT NULL,
    organic_impressions bigint NULL,
    sponsored_impressions bigint NULL,
    listing_opens bigint NULL,
    website_clicks bigint NULL,
    phone_clicks bigint NULL,
    whats_app_clicks bigint NULL,
    email_clicks bigint NULL,
    map_clicks bigint NULL,
    external_profile_clicks bigint NULL,
    unavailable_reason varchar(1000) NULL,
    PRIMARY KEY (metric_date, catalog_key, listing_id),
    CONSTRAINT ck_analytics_daily_metric_listing_id CHECK
    (
        listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT ck_analytics_daily_metric_catalog_key CHECK
    (
        catalog_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'
    ),
    CONSTRAINT ck_analytics_daily_metric_source_count CHECK
    (
        source_read_revision_count >= 0
    ),
    CONSTRAINT ck_analytics_daily_metric_readiness CHECK
    (
        readiness_state BETWEEN 1 AND 4
    ),
    CONSTRAINT ck_analytics_daily_metric_digest CHECK
    (
        aggregation_source_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT ck_analytics_daily_metric_value_shape CHECK
    (
        (
            readiness_state = 1
            AND unavailable_reason IS NULL
            AND organic_impressions IS NOT NULL
            AND sponsored_impressions IS NOT NULL
            AND listing_opens IS NOT NULL
            AND website_clicks IS NOT NULL
            AND phone_clicks IS NOT NULL
            AND whats_app_clicks IS NOT NULL
            AND email_clicks IS NOT NULL
            AND map_clicks IS NOT NULL
            AND external_profile_clicks IS NOT NULL
        )
        OR
        (
            readiness_state <> 1
            AND length(btrim(unavailable_reason)) > 0
            AND organic_impressions IS NULL
            AND sponsored_impressions IS NULL
            AND listing_opens IS NULL
            AND website_clicks IS NULL
            AND phone_clicks IS NULL
            AND whats_app_clicks IS NULL
            AND email_clicks IS NULL
            AND map_clicks IS NULL
            AND external_profile_clicks IS NULL
        )
    ),
    CONSTRAINT ck_analytics_daily_metric_nonnegative CHECK
    (
        (organic_impressions IS NULL OR organic_impressions >= 0)
        AND (sponsored_impressions IS NULL OR sponsored_impressions >= 0)
        AND (listing_opens IS NULL OR listing_opens >= 0)
        AND (website_clicks IS NULL OR website_clicks >= 0)
        AND (phone_clicks IS NULL OR phone_clicks >= 0)
        AND (whats_app_clicks IS NULL OR whats_app_clicks >= 0)
        AND (email_clicks IS NULL OR email_clicks >= 0)
        AND (map_clicks IS NULL OR map_clicks >= 0)
        AND (external_profile_clicks IS NULL OR external_profile_clicks >= 0)
    )
);

CREATE INDEX ix_daily_listing_metric_listing_id_metric_date
    ON aggregates.daily_listing_metric (listing_id, metric_date);
