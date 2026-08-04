CREATE TABLE batches.item_decision_current
(
    batch_id uuid NOT NULL,
    item_key text NOT NULL,
    decision integer NOT NULL,
    reason_codes text[] NOT NULL,
    decision_revision bigint NOT NULL,
    actor_identity text NOT NULL,
    decided_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_item_decision_current PRIMARY KEY (batch_id, item_key),
    CONSTRAINT fk_item_decision_current_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_item_decision_current_item_key
        CHECK (length(item_key) BETWEEN 1 AND 300 AND item_key !~ '[[:cntrl:]]'),
    CONSTRAINT ck_item_decision_current_decision
        CHECK (decision BETWEEN 1 AND 3),
    CONSTRAINT ck_item_decision_current_reason_codes
        CHECK (cardinality(reason_codes) BETWEEN 1 AND 50),
    CONSTRAINT ck_item_decision_current_revision
        CHECK (decision_revision > 0),
    CONSTRAINT ck_item_decision_current_actor
        CHECK (length(actor_identity) BETWEEN 1 AND 200)
);

CREATE INDEX ix_item_decision_current_batch_decision
    ON batches.item_decision_current (batch_id, decision, item_key);

CREATE TABLE batches.item_decision_history
(
    decision_id uuid PRIMARY KEY,
    batch_id uuid NOT NULL,
    item_key text NOT NULL,
    previous_decision_revision bigint NULL,
    decision_revision bigint NOT NULL,
    decision integer NOT NULL,
    reason_codes text[] NOT NULL,
    actor_identity text NOT NULL,
    decided_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT uq_item_decision_history_revision
        UNIQUE (batch_id, item_key, decision_revision),
    CONSTRAINT fk_item_decision_history_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_item_decision_history_id
        CHECK (decision_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_item_decision_history_item_key
        CHECK (length(item_key) BETWEEN 1 AND 300 AND item_key !~ '[[:cntrl:]]'),
    CONSTRAINT ck_item_decision_history_previous_revision
        CHECK (previous_decision_revision IS NULL OR previous_decision_revision > 0),
    CONSTRAINT ck_item_decision_history_revision
        CHECK (
            decision_revision > 0
            AND (previous_decision_revision IS NULL OR decision_revision = previous_decision_revision + 1)),
    CONSTRAINT ck_item_decision_history_decision
        CHECK (decision BETWEEN 1 AND 3),
    CONSTRAINT ck_item_decision_history_reason_codes
        CHECK (cardinality(reason_codes) BETWEEN 1 AND 50),
    CONSTRAINT ck_item_decision_history_actor
        CHECK (length(actor_identity) BETWEEN 1 AND 200)
);

CREATE INDEX ix_item_decision_history_batch_item
    ON batches.item_decision_history (batch_id, item_key, decision_revision);

CREATE TABLE batches.commit_selection
(
    batch_id uuid NOT NULL,
    item_key text NOT NULL,
    command_scope text NOT NULL,
    command_key text NOT NULL,
    actor_identity text NOT NULL,
    selected_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_commit_selection PRIMARY KEY (batch_id, item_key),
    CONSTRAINT fk_commit_selection_batch
        FOREIGN KEY (batch_id)
        REFERENCES batches.import_batch (id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_commit_selection_item_key
        CHECK (length(item_key) BETWEEN 1 AND 300 AND item_key !~ '[[:cntrl:]]'),
    CONSTRAINT ck_commit_selection_scope
        CHECK (length(command_scope) BETWEEN 1 AND 150),
    CONSTRAINT ck_commit_selection_key
        CHECK (length(command_key) BETWEEN 1 AND 200),
    CONSTRAINT ck_commit_selection_actor
        CHECK (length(actor_identity) BETWEEN 1 AND 200)
);

CREATE TABLE batches.catalog_delivery_outcome
(
    batch_id uuid NOT NULL,
    item_key text NOT NULL,
    catalog_command_id uuid NOT NULL,
    outcome integer NOT NULL,
    catalog_subject_id uuid NULL,
    catalog_listing_id uuid NULL,
    catalog_listing_revision_id uuid NULL,
    failure_code text NULL,
    actor_identity text NOT NULL,
    recorded_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_catalog_delivery_outcome PRIMARY KEY (batch_id, item_key),
    CONSTRAINT uq_catalog_delivery_outcome_command UNIQUE (catalog_command_id),
    CONSTRAINT fk_catalog_delivery_outcome_selection
        FOREIGN KEY (batch_id, item_key)
        REFERENCES batches.commit_selection (batch_id, item_key)
        ON DELETE RESTRICT,
    CONSTRAINT ck_catalog_delivery_outcome_command_id
        CHECK (catalog_command_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_catalog_delivery_outcome_value
        CHECK (outcome BETWEEN 1 AND 2),
    CONSTRAINT ck_catalog_delivery_outcome_identity
        CHECK (
            (outcome = 1
             AND catalog_subject_id IS NOT NULL
             AND catalog_subject_id <> '00000000-0000-0000-0000-000000000000'::uuid
             AND catalog_listing_id IS NOT NULL
             AND catalog_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid
             AND catalog_listing_revision_id IS NOT NULL
             AND catalog_listing_revision_id <> '00000000-0000-0000-0000-000000000000'::uuid
             AND failure_code IS NULL)
            OR
            (outcome = 2
             AND catalog_subject_id IS NULL
             AND catalog_listing_id IS NULL
             AND catalog_listing_revision_id IS NULL
             AND failure_code IS NOT NULL
             AND length(failure_code) BETWEEN 1 AND 200)),
    CONSTRAINT ck_catalog_delivery_outcome_actor
        CHECK (length(actor_identity) BETWEEN 1 AND 200)
);

CREATE TRIGGER trg_item_decision_history_immutable
    BEFORE UPDATE OR DELETE ON batches.item_decision_history
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();

CREATE TRIGGER trg_commit_selection_immutable
    BEFORE UPDATE OR DELETE ON batches.commit_selection
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();

CREATE TRIGGER trg_catalog_delivery_outcome_immutable
    BEFORE UPDATE OR DELETE ON batches.catalog_delivery_outcome
    FOR EACH ROW EXECUTE FUNCTION operations.reject_immutable_ingestion_record();
