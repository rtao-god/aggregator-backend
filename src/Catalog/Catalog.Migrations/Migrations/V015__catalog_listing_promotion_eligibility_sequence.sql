CREATE TABLE catalog.listing_promotion_eligibility_sequence
(
    catalog_key varchar(96) NOT NULL,
    listing_id uuid NOT NULL,
    next_revision bigint NOT NULL,
    CONSTRAINT listing_promotion_eligibility_sequence_pk
        PRIMARY KEY (catalog_key, listing_id),
    CONSTRAINT listing_promotion_eligibility_sequence_revision_positive
        CHECK (next_revision >= 2)
);

ALTER TABLE catalog.listing
    ADD CONSTRAINT listing_catalog_id_unique
    UNIQUE (catalog_key, id);

ALTER TABLE catalog.listing_promotion_eligibility_sequence
    ADD CONSTRAINT listing_promotion_eligibility_sequence_listing_fk
    FOREIGN KEY (catalog_key, listing_id)
    REFERENCES catalog.listing (catalog_key, id)
    ON DELETE RESTRICT;
