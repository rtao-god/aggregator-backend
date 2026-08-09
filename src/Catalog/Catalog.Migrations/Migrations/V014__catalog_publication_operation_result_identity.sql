ALTER TABLE catalog.publication_operation
    ADD CONSTRAINT publication_operation_result_identity_consistent CHECK
    (
        result_publication_id IS NULL
        OR result_publication_id = publication_id
    );
