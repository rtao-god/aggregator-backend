CREATE SCHEMA IF NOT EXISTS promotion;
CREATE SCHEMA IF NOT EXISTS promotion_operations;
CREATE SCHEMA IF NOT EXISTS promotion_projection;

CREATE TABLE promotion_projection.eligibility
(
    product_revision_id uuid NOT NULL,
    product_revision_active boolean NOT NULL,
    entitlement_id uuid NOT NULL,
    entitlement_active boolean NOT NULL,
    listing_id uuid NOT NULL,
    listing_eligible boolean NOT NULL,
    catalog_key text NOT NULL,
    placement_key text NOT NULL,
    placement_capacity_limit integer NOT NULL,
    projection_revision bigint NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_promotion_eligibility PRIMARY KEY
        (product_revision_id, entitlement_id, listing_id, catalog_key, placement_key),
    CONSTRAINT ck_promotion_eligibility_product_id CHECK (product_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_promotion_eligibility_entitlement_id CHECK (entitlement_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_promotion_eligibility_listing_id CHECK (listing_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_promotion_eligibility_catalog_key CHECK (catalog_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_promotion_eligibility_placement_key CHECK (placement_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_promotion_eligibility_capacity CHECK (placement_capacity_limit BETWEEN 1 AND 10000),
    CONSTRAINT ck_promotion_eligibility_revision CHECK (projection_revision > 0)
);

CREATE INDEX ix_promotion_eligibility_catalog_placement
    ON promotion_projection.eligibility (catalog_key, placement_key);

CREATE TABLE promotion.campaign
(
    id uuid PRIMARY KEY,
    product_revision_id uuid NOT NULL,
    entitlement_id uuid NOT NULL,
    listing_id uuid NOT NULL,
    catalog_key text NOT NULL,
    placement_key text NOT NULL,
    capacity_units integer NOT NULL,
    starts_at_utc timestamp with time zone NOT NULL,
    ends_at_utc timestamp with time zone NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    last_changed_at_utc timestamp with time zone NOT NULL,
    state integer NOT NULL,
    aggregate_revision bigint NOT NULL,
    suspension_reason text NULL,
    CONSTRAINT ck_promotion_campaign_id CHECK (id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_promotion_campaign_product_id CHECK (product_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_promotion_campaign_entitlement_id CHECK (entitlement_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_promotion_campaign_listing_id CHECK (listing_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_promotion_campaign_catalog_key CHECK (catalog_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_promotion_campaign_placement_key CHECK (placement_key ~ '^[a-z][a-z0-9-]{0,95}$'),
    CONSTRAINT ck_promotion_campaign_capacity CHECK (capacity_units BETWEEN 1 AND 100),
    CONSTRAINT ck_promotion_campaign_window CHECK (ends_at_utc > starts_at_utc),
    CONSTRAINT ck_promotion_campaign_window_limit CHECK (ends_at_utc <= starts_at_utc + interval '366 days'),
    CONSTRAINT ck_promotion_campaign_state CHECK (state BETWEEN 1 AND 5),
    CONSTRAINT ck_promotion_campaign_revision CHECK (aggregate_revision > 0),
    CONSTRAINT ck_promotion_campaign_time_order CHECK (last_changed_at_utc >= created_at_utc),
    CONSTRAINT ck_promotion_campaign_suspension_reason CHECK
        ((state = 3 AND suspension_reason IS NOT NULL AND length(suspension_reason) BETWEEN 1 AND 300)
         OR (state <> 3 AND suspension_reason IS NULL))
);

CREATE INDEX ix_promotion_campaign_capacity_window
    ON promotion.campaign
    (catalog_key, placement_key, state, starts_at_utc, ends_at_utc);

CREATE INDEX ix_promotion_campaign_listing_state
    ON promotion.campaign (listing_id, state);

CREATE TABLE promotion_operations.command_result
(
    scope text NOT NULL,
    key text NOT NULL,
    request_digest character(64) NOT NULL,
    campaign_id uuid NOT NULL,
    result_document bytea NOT NULL,
    result_digest character(64) NOT NULL,
    caller_identity text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_promotion_command_result PRIMARY KEY (scope, key),
    CONSTRAINT fk_promotion_command_campaign
        FOREIGN KEY (campaign_id)
        REFERENCES promotion.campaign (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_promotion_command_scope CHECK (length(scope) BETWEEN 1 AND 150),
    CONSTRAINT ck_promotion_command_key CHECK (length(key) BETWEEN 1 AND 200),
    CONSTRAINT ck_promotion_command_request_digest CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_promotion_command_result_document CHECK (octet_length(result_document) > 0),
    CONSTRAINT ck_promotion_command_result_digest CHECK (result_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_promotion_command_caller CHECK (length(caller_identity) BETWEEN 1 AND 200)
);

CREATE OR REPLACE FUNCTION promotion.reject_campaign_identity_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.id <> NEW.id
       OR OLD.product_revision_id <> NEW.product_revision_id
       OR OLD.entitlement_id <> NEW.entitlement_id
       OR OLD.listing_id <> NEW.listing_id
       OR OLD.catalog_key <> NEW.catalog_key
       OR OLD.placement_key <> NEW.placement_key
       OR OLD.capacity_units <> NEW.capacity_units
       OR OLD.starts_at_utc <> NEW.starts_at_utc
       OR OLD.ends_at_utc <> NEW.ends_at_utc
       OR OLD.created_at_utc <> NEW.created_at_utc
    THEN
        RAISE EXCEPTION 'immutable Promotion campaign identity may not change'
            USING ERRCODE = '55000';
    END IF;

    IF NEW.aggregate_revision <> OLD.aggregate_revision + 1
    THEN
        RAISE EXCEPTION 'Promotion campaign aggregate revision must advance exactly once'
            USING ERRCODE = '40001';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_promotion_campaign_identity_immutable
    BEFORE UPDATE ON promotion.campaign
    FOR EACH ROW EXECUTE FUNCTION promotion.reject_campaign_identity_mutation();

CREATE OR REPLACE FUNCTION promotion_operations.reject_immutable_command_result()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Promotion command results are immutable'
        USING ERRCODE = '55000';
END;
$$;

CREATE TRIGGER trg_promotion_command_result_immutable
    BEFORE UPDATE OR DELETE ON promotion_operations.command_result
    FOR EACH ROW EXECUTE FUNCTION promotion_operations.reject_immutable_command_result();
