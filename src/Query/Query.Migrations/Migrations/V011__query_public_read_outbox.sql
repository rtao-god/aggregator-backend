CREATE TABLE messaging.outbox_message
(
    message_id uuid PRIMARY KEY,
    routing_key text NOT NULL,
    contract_identity text NOT NULL,
    payload_json text NOT NULL,
    payload_digest char(64) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    correlation_id varchar(128) NOT NULL,
    causation_id uuid NOT NULL,
    delivery_attempts integer NOT NULL DEFAULT 0,
    lease_token uuid NULL,
    leased_by varchar(200) NULL,
    lease_expires_at_utc timestamptz NULL,
    dispatched_at_utc timestamptz NULL,
    dead_lettered_at_utc timestamptz NULL,
    dead_letter_reason varchar(2000) NULL,
    last_error varchar(2000) NULL,
    CONSTRAINT query_outbox_message_id_nonempty CHECK
    (
        message_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT query_outbox_payload_json_valid CHECK (payload_json IS JSON OBJECT),
    CONSTRAINT query_outbox_payload_digest_valid CHECK
    (
        payload_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT query_outbox_correlation_present CHECK
    (
        length(btrim(correlation_id)) > 0
    ),
    CONSTRAINT query_outbox_routing_key_present CHECK
    (
        length(btrim(routing_key)) > 0
    ),
    CONSTRAINT query_outbox_contract_identity_present CHECK
    (
        length(btrim(contract_identity)) > 0
    ),
    CONSTRAINT query_outbox_causation_nonempty CHECK
    (
        causation_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT query_outbox_attempts_nonnegative CHECK
    (
        delivery_attempts >= 0
    ),
    CONSTRAINT query_outbox_lease_consistent CHECK
    (
        (
            lease_token IS NULL
            AND leased_by IS NULL
            AND lease_expires_at_utc IS NULL
        )
        OR
        (
            lease_token IS NOT NULL
            AND leased_by IS NOT NULL
            AND lease_expires_at_utc IS NOT NULL
        )
    ),
    CONSTRAINT query_outbox_terminal_state_exclusive CHECK
    (
        dispatched_at_utc IS NULL
        OR dead_lettered_at_utc IS NULL
    ),
    CONSTRAINT query_outbox_dead_letter_reason_consistent CHECK
    (
        (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NULL)
        OR
        (dead_lettered_at_utc IS NOT NULL AND length(btrim(dead_letter_reason)) > 0)
    )
);

CREATE INDEX query_outbox_dispatch_idx
    ON messaging.outbox_message (occurred_at_utc, message_id)
    WHERE dispatched_at_utc IS NULL
      AND dead_lettered_at_utc IS NULL;

CREATE INDEX query_outbox_lease_expiry_idx
    ON messaging.outbox_message (lease_expires_at_utc)
    WHERE dispatched_at_utc IS NULL
      AND dead_lettered_at_utc IS NULL
      AND lease_token IS NOT NULL;
