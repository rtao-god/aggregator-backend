CREATE SCHEMA IF NOT EXISTS ingestion;

CREATE TABLE ingestion.collector_candidate
(
    candidate_id uuid PRIMARY KEY,
    subject_id uuid NOT NULL,
    subject_revision_id uuid NOT NULL,
    source_system varchar(96) NOT NULL,
    source_reference varchar(2048) NOT NULL,
    observed_at_utc timestamptz NOT NULL,
    kind integer NOT NULL,
    external_id varchar(256) NOT NULL,
    title varchar(300) NOT NULL,
    website varchar(2048) NOT NULL,
    hourly_price numeric(24, 8) NULL,
    evidence_digest char(64) NOT NULL,
    content_digest char(64) NOT NULL,
    accepted_at_utc timestamptz NOT NULL,
    CONSTRAINT collector_candidate_source_system_shape
        CHECK (source_system ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT collector_candidate_kind_valid
        CHECK (kind IN (1, 2)),
    CONSTRAINT collector_candidate_website_shape
        CHECK (website ~ '^https?://'),
    CONSTRAINT collector_candidate_hourly_price_bounds
        CHECK (hourly_price IS NULL OR hourly_price BETWEEN 0 AND 1000000),
    CONSTRAINT collector_candidate_evidence_digest_shape
        CHECK (evidence_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT collector_candidate_content_digest_shape
        CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT collector_candidate_time_order
        CHECK (accepted_at_utc >= observed_at_utc),
    CONSTRAINT collector_candidate_source_identity_unique
        UNIQUE (source_system, external_id),
    CONSTRAINT collector_candidate_subject_revision_unique
        UNIQUE (subject_id, subject_revision_id)
);

CREATE INDEX collector_candidate_accepted_idx
    ON ingestion.collector_candidate (accepted_at_utc DESC, candidate_id);
CREATE INDEX collector_candidate_subject_idx
    ON ingestion.collector_candidate (subject_id, accepted_at_utc DESC);

CREATE TABLE ingestion.collector_command
(
    command_id uuid PRIMARY KEY,
    command_digest char(64) NOT NULL,
    candidate_id uuid NOT NULL
        REFERENCES ingestion.collector_candidate(candidate_id),
    committed_at_utc timestamptz NOT NULL,
    CONSTRAINT collector_command_digest_shape
        CHECK (command_digest ~ '^[0-9a-f]{64}$')
);

CREATE INDEX collector_command_candidate_idx
    ON ingestion.collector_command (candidate_id, committed_at_utc DESC);
