#!/usr/bin/env bash
set -euo pipefail

: "${PGHOST:?PGHOST is required}"
: "${PGPORT:?PGPORT is required}"
: "${PGDATABASE:?PGDATABASE is required}"
: "${PGUSER:?PGUSER is required}"
: "${PGPASSWORD:?PGPASSWORD is required}"
: "${APP_ROLE:?APP_ROLE is required}"

psql \
  --host "$PGHOST" \
  --port "$PGPORT" \
  --username "$PGUSER" \
  --dbname "$PGDATABASE" \
  --set=ON_ERROR_STOP=1 \
  --set=app_role="$APP_ROLE" <<'SQL'
SELECT format('GRANT USAGE ON SCHEMA %I TO %I', schema_name, :'app_role')
FROM information_schema.schemata
WHERE schema_name NOT IN ('pg_catalog', 'information_schema')
  AND schema_name NOT LIKE 'pg_toast%' \gexec

SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', schema_name, :'app_role')
FROM information_schema.schemata
WHERE schema_name NOT IN ('pg_catalog', 'information_schema')
  AND schema_name NOT LIKE 'pg_toast%' \gexec

SELECT format('GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO %I', schema_name, :'app_role')
FROM information_schema.schemata
WHERE schema_name NOT IN ('pg_catalog', 'information_schema')
  AND schema_name NOT LIKE 'pg_toast%' \gexec

SELECT format('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA %I TO %I', schema_name, :'app_role')
FROM information_schema.schemata
WHERE schema_name NOT IN ('pg_catalog', 'information_schema')
  AND schema_name NOT LIKE 'pg_toast%' \gexec
SQL
