DO $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM catalog.outbox_message
        WHERE
            (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NOT NULL)
            OR
            (
                dead_lettered_at_utc IS NOT NULL
                AND
                (
                    dead_letter_reason IS NULL
                    OR length(btrim(dead_letter_reason)) = 0
                )
            )
    )
    THEN
        RAISE EXCEPTION USING
            MESSAGE = 'Catalog outbox dead-letter migration is blocked by an incomplete terminal state.',
            HINT = 'Repair each row so dead-letter timestamp and non-empty reason are either both present or both absent.';
    END IF;
END
$$;

ALTER TABLE catalog.outbox_message
    DROP CONSTRAINT ck_catalog_outbox_dead_letter_shape;

ALTER TABLE catalog.outbox_message
    ADD CONSTRAINT ck_catalog_outbox_dead_letter_shape CHECK
    (
        (dead_lettered_at_utc IS NULL AND dead_letter_reason IS NULL)
        OR
        (
            dead_lettered_at_utc IS NOT NULL
            AND dead_letter_reason IS NOT NULL
            AND length(btrim(dead_letter_reason)) > 0
        )
    );
