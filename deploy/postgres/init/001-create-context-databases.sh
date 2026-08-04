#!/usr/bin/env bash
set -euo pipefail

required=(
  CATALOG_APP_PASSWORD CATALOG_MIGRATOR_PASSWORD
  QUERY_APP_PASSWORD QUERY_MIGRATOR_PASSWORD
  INGESTION_APP_PASSWORD INGESTION_MIGRATOR_PASSWORD
  ANALYTICS_APP_PASSWORD ANALYTICS_MIGRATOR_PASSWORD
  PROMOTION_APP_PASSWORD PROMOTION_MIGRATOR_PASSWORD
)
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "Required database secret ${name} is missing" >&2
    exit 1
  fi
done

create_context() {
  local context="$1"
  local database="$2"
  local app_role="$3"
  local app_password="$4"
  local migrator_role="$5"
  local migrator_password="$6"

  psql --username "$POSTGRES_USER" --dbname postgres \
    --set=context="$context" \
    --set=database="$database" \
    --set=app_role="$app_role" \
    --set=app_password="$app_password" \
    --set=migrator_role="$migrator_role" \
    --set=migrator_password="$migrator_password" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'migrator_role', :'migrator_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'migrator_role') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'app_role', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app_role') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', :'database', :'migrator_role')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'database') \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'database') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'database', :'app_role') \gexec
SELECT format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO %I', :'database', :'migrator_role') \gexec
SQL

  psql --username "$POSTGRES_USER" --dbname "$database" \
    --set=app_role="$app_role" \
    --set=migrator_role="$migrator_role" <<'SQL'
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I',
              :'migrator_role', :'app_role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I',
              :'migrator_role', :'app_role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT EXECUTE ON FUNCTIONS TO %I',
              :'migrator_role', :'app_role') \gexec
SQL
}

create_context catalog catalog_db catalog_app "$CATALOG_APP_PASSWORD" catalog_migrator "$CATALOG_MIGRATOR_PASSWORD"
create_context query query_db query_app "$QUERY_APP_PASSWORD" query_migrator "$QUERY_MIGRATOR_PASSWORD"
create_context ingestion ingestion_db ingestion_app "$INGESTION_APP_PASSWORD" ingestion_migrator "$INGESTION_MIGRATOR_PASSWORD"
create_context analytics analytics_db analytics_app "$ANALYTICS_APP_PASSWORD" analytics_migrator "$ANALYTICS_MIGRATOR_PASSWORD"
create_context promotion promotion_db promotion_app "$PROMOTION_APP_PASSWORD" promotion_migrator "$PROMOTION_MIGRATOR_PASSWORD"

psql --username "$POSTGRES_USER" --dbname query_db <<'SQL'
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
SQL
