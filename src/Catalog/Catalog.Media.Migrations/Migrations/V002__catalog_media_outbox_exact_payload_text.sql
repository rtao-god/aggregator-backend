DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM media_messaging.outbox_message) THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog Media outbox payload storage migration is blocked because existing jsonb rows cannot prove their original UTF-8 payload bytes.',
            HINT = 'Drain or re-materialize every Catalog Media outbox message through its producer owner before applying V002.';
    END IF;
END
$$;

ALTER TABLE media_messaging.outbox_message
    ALTER COLUMN payload_json TYPE text
    USING payload_json::text;
