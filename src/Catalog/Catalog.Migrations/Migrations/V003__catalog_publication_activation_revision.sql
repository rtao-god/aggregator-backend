CREATE TABLE catalog.publication_activation_sequence
(
    catalog_key text PRIMARY KEY,
    next_revision bigint NOT NULL CHECK (next_revision >= 2)
);
