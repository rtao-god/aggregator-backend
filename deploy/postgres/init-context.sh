#!/bin/sh
set -eu

require_identifier() {
  value="$1"
  name="$2"
  case "$value" in
    ''|*[!a-z0-9_]*)
      echo "$name must contain only lowercase letters, digits and underscores." >&2
      exit 64
      ;;
  esac
}

for variable in CONTEXT_DB CONTEXT_MIGRATOR_USER CONTEXT_MIGRATOR_PASSWORD CONTEXT_APP_USER CONTEXT_APP_PASSWORD; do
  eval "value=\${$variable:-}"
  if [ -z "$value" ]; then
    echo "$variable is required." >&2
    exit 64
  fi
done

require_identifier "$CONTEXT_DB" CONTEXT_DB
require_identifier "$CONTEXT_MIGRATOR_USER" CONTEXT_MIGRATOR_USER
require_identifier "$CONTEXT_APP_USER" CONTEXT_APP_USER

psql --set=ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname postgres \
  --set=context_db="$CONTEXT_DB" \
  --set=migrator_user="$CONTEXT_MIGRATOR_USER" \
  --set=migrator_password="$CONTEXT_MIGRATOR_PASSWORD" \
  --set=app_user="$CONTEXT_APP_USER" \
  --set=app_password="$CONTEXT_APP_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'migrator_user', :'migrator_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'migrator_user') \gexec

SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'app_user', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app_user') \gexec

SELECT format('CREATE DATABASE %I OWNER %I', :'context_db', :'migrator_user')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'context_db') \gexec

SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'context_db') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'context_db', :'app_user') \gexec
SQL

psql --set=ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname "$CONTEXT_DB" \
  --set=migrator_user="$CONTEXT_MIGRATOR_USER" \
  --set=app_user="$CONTEXT_APP_USER" <<'SQL'
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'app_user') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', :'migrator_user', :'app_user') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT USAGE, SELECT ON SEQUENCES TO %I', :'migrator_user', :'app_user') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT EXECUTE ON FUNCTIONS TO %I', :'migrator_user', :'app_user') \gexec
SQL
