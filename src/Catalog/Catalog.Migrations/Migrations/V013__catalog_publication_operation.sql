CREATE TABLE catalog.publication_operation
(
    id uuid PRIMARY KEY,
    publication_id uuid NOT NULL,
    publication_sequence bigint NOT NULL,
    catalog_key varchar(96) NOT NULL,
    actor_id uuid NOT NULL,
    idempotency_key varchar(128) NOT NULL,
    request_document bytea NOT NULL,
    request_digest char(64) NOT NULL,
    correlation_id varchar(128) NOT NULL,
    causation_id uuid NULL,
    state integer NOT NULL,
    attempt integer NOT NULL DEFAULT 0,
    lease_token uuid NULL,
    leased_by varchar(200) NULL,
    lease_expires_at_utc timestamptz NULL,
    next_attempt_at_utc timestamptz NULL,
    result_publication_id uuid NULL,
    failure_owner varchar(200) NULL,
    failure_code varchar(200) NULL,
    failure_detail varchar(4000) NULL,
    failure_required_action varchar(2000) NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    CONSTRAINT publication_operation_publication_id_unique UNIQUE (publication_id),
    CONSTRAINT publication_operation_publication_sequence_unique UNIQUE
    (
        catalog_key,
        publication_sequence
    ),
    CONSTRAINT publication_operation_publication_sequence_valid CHECK
    (
        publication_sequence > 0
    ),
    CONSTRAINT publication_operation_idempotency_unique UNIQUE
    (
        catalog_key,
        actor_id,
        idempotency_key
    ),
    CONSTRAINT publication_operation_result_fk FOREIGN KEY (result_publication_id)
        REFERENCES catalog.publication (id),
    CONSTRAINT publication_operation_request_present CHECK
    (
        octet_length(request_document) > 0
    ),
    CONSTRAINT publication_operation_request_digest_valid CHECK
    (
        request_digest ~ '^[0-9a-f]{64}$'
    ),
    CONSTRAINT publication_operation_state_valid CHECK
    (
        state BETWEEN 1 AND 5
    ),
    CONSTRAINT publication_operation_attempt_valid CHECK
    (
        attempt >= 0
    ),
    CONSTRAINT publication_operation_time_valid CHECK
    (
        updated_at_utc >= created_at_utc
    ),
    CONSTRAINT publication_operation_lease_consistent CHECK
    (
        (
            state = 2
            AND lease_token IS NOT NULL
            AND leased_by IS NOT NULL
            AND lease_expires_at_utc IS NOT NULL
        )
        OR
        (
            state <> 2
            AND lease_token IS NULL
            AND leased_by IS NULL
            AND lease_expires_at_utc IS NULL
        )
    ),
    CONSTRAINT publication_operation_retry_consistent CHECK
    (
        (state = 3 AND next_attempt_at_utc IS NOT NULL)
        OR (state <> 3 AND next_attempt_at_utc IS NULL)
    ),
    CONSTRAINT publication_operation_result_consistent CHECK
    (
        (state = 4 AND result_publication_id IS NOT NULL)
        OR (state <> 4 AND result_publication_id IS NULL)
    ),
    CONSTRAINT publication_operation_failure_tuple_consistent CHECK
    (
        (
            failure_owner IS NULL
            AND failure_code IS NULL
            AND failure_detail IS NULL
            AND failure_required_action IS NULL
        )
        OR
        (
            failure_owner IS NOT NULL
            AND failure_code IS NOT NULL
            AND failure_detail IS NOT NULL
            AND failure_required_action IS NOT NULL
        )
    ),
    CONSTRAINT publication_operation_terminal_failure_consistent CHECK
    (
        state <> 5
        OR failure_owner IS NOT NULL
    ),
    CONSTRAINT publication_operation_pending_clean CHECK
    (
        state <> 1
        OR
        (
            attempt = 0
            AND failure_owner IS NULL
        )
    ),
    CONSTRAINT publication_operation_completed_clean CHECK
    (
        state <> 4
        OR failure_owner IS NULL
    )
);

CREATE INDEX publication_operation_claim_idx
    ON catalog.publication_operation (state, next_attempt_at_utc, created_at_utc, id);

CREATE INDEX publication_operation_lease_expiry_idx
    ON catalog.publication_operation (lease_expires_at_utc)
    WHERE state = 2;
