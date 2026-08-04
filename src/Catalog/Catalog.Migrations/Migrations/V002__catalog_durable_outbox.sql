DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM catalog.outbox_message) THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog outbox migration is blocked because legacy rows lack canonical payload digests and correlation metadata.',
            HINT = 'Drain or explicitly re-materialize every legacy event from its Catalog owner before applying V002.';
    END IF;
END
$$;

DROP TABLE catalog.outbox_message;

CREATE TABLE catalog.outbox_message
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
    CONSTRAINT ck_catalog_outbox_message_id CHECK (message_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_catalog_outbox_routing_key CHECK (length(btrim(routing_key)) > 0),
    CONSTRAINT ck_catalog_outbox_contract_identity CHECK (length(btrim(contract_identity)) > 0),
    CONSTRAINT ck_catalog_outbox_payload_digest CHECK (payload_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_catalog_outbox_correlation_id CHECK (correlation_id ~ '^[A-Za-z0-9_.:-]{8,128}$'),
    CONSTRAINT ck_catalog_outbox_causation_id CHECK (
        causation_id IS NULL OR causation_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_catalog_outbox_delivery_attempts CHECK (delivery_attempts >= 0),
    CONSTRAINT ck_catalog_outbox_lease_shape CHECK
    (
        (lease_token IS NULL AND leased_by IS NULL AND lease_expires_at_utc IS NULL)
        OR
        (lease_token IS NOT NULL AND leased_by IS NOT NULL AND lease_expires_at_utc IS NOT NULL)
    ),
    CONSTRAINT ck_catalog_outbox_terminal_state CHECK
    (
        NOT (dispatched_at_utc IS NOT NULL AND dead_lettered_at_utc IS NOT NULL)
    ),
    CONSTRAINT ck_catalog_outbox_dead_letter_shape CHECK
    (
        (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NULL)
        OR
        (dead_lettered_at_utc IS NOT NULL AND length(btrim(dead_letter_reason)) > 0)
    )
);

CREATE INDEX ix_catalog_outbox_pending
    ON catalog.outbox_message (occurred_at_utc, message_id)
    WHERE dispatched_at_utc IS NULL AND dead_lettered_at_utc IS NULL;

CREATE INDEX ix_catalog_outbox_lease_expiry
    ON catalog.outbox_message (lease_expires_at_utc)
    WHERE dispatched_at_utc IS NULL
      AND dead_lettered_at_utc IS NULL
      AND lease_expires_at_utc IS NOT NULL;
