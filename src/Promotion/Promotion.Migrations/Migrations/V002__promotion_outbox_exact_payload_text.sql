DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM messaging.outbox_message) THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Promotion outbox payload storage migration is blocked because existing jsonb rows cannot prove their original UTF-8 payload bytes.',
            HINT = 'Drain or re-materialize every Promotion outbox message through its producer owner before applying V002.';
    END IF;
END
$$;

ALTER TABLE messaging.outbox_message
    ALTER COLUMN payload_json TYPE text
    USING payload_json::text;
