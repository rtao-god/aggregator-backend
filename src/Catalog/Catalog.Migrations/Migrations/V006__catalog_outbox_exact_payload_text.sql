DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM catalog.outbox_message) THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog outbox payload storage migration is blocked because existing jsonb rows cannot prove their original UTF-8 payload bytes.',
            HINT = 'Drain or re-materialize every Catalog outbox message through its producer owner before applying V006.';
    END IF;
END
$$;

ALTER TABLE catalog.outbox_message
    ALTER COLUMN payload_json TYPE text
    USING payload_json::text;
