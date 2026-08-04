#!/bin/sh
set -eu

for variable in DB_HOST CONTEXT_DB CONTEXT_MIGRATOR_USER CONTEXT_MIGRATOR_PASSWORD CONTEXT_APP_USER; do
  eval "value=\${$variable:-}"
  if [ -z "$value" ]; then
    echo "$variable is required." >&2
    exit 64
  fi
done

case "$CONTEXT_APP_USER" in
  *[!a-z0-9_]*)
    echo "CONTEXT_APP_USER must be a lowercase PostgreSQL identifier." >&2
    exit 64
    ;;
esac

export PGPASSWORD="$CONTEXT_MIGRATOR_PASSWORD"
psql --set=ON_ERROR_STOP=1 \
  --host "$DB_HOST" \
  --username "$CONTEXT_MIGRATOR_USER" \
  --dbname "$CONTEXT_DB" \
  --set=app_user="$CONTEXT_APP_USER" <<'SQL'
SELECT format('GRANT USAGE ON SCHEMA %I TO %I', nspname, :'app_user')
FROM pg_namespace
WHERE nspname NOT LIKE 'pg_%'
  AND nspname <> 'information_schema' \gexec

SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', nspname, :'app_user')
FROM pg_namespace
WHERE nspname NOT LIKE 'pg_%'
  AND nspname <> 'information_schema' \gexec

SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', nspname, :'app_user')
FROM pg_namespace
WHERE nspname NOT LIKE 'pg_%'
  AND nspname <> 'information_schema' \gexec

SELECT format('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA %I TO %I', nspname, :'app_user')
FROM pg_namespace
WHERE nspname NOT LIKE 'pg_%'
  AND nspname <> 'information_schema' \gexec
SQL
