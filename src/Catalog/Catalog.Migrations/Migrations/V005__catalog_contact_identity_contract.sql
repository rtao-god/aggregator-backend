ALTER TABLE catalog.contact
    ADD CONSTRAINT contact_id_nonempty
    CHECK (id <> '00000000-0000-0000-0000-000000000000'::uuid);
