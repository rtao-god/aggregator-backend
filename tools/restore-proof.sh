#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKUP_DIR="${1:-${ROOT_DIR}/artifacts/backup}"

compose() {
  docker compose \
    -f "${ROOT_DIR}/compose.yml" \
    -f "${ROOT_DIR}/compose.runtime.yml" \
    -f "${ROOT_DIR}/compose.acceptance.yml" \
    "$@"
}

if [[ ! -f "${BACKUP_DIR}/SHA256SUMS" ]]; then
  echo "Backup manifest is missing: ${BACKUP_DIR}/SHA256SUMS" >&2
  exit 1
fi
(
  cd "${BACKUP_DIR}"
  sha256sum --check SHA256SUMS
)

restore_database() {
  local service="$1"
  local user="$2"
  local source_database="$3"
  local proof_database="${source_database}_restore_proof"
  local dump="${BACKUP_DIR}/${source_database}.dump"
  test -s "${dump}"

  compose exec -T "${service}" \
    dropdb --username="${user}" --if-exists "${proof_database}"
  compose exec -T "${service}" \
    createdb --username="${user}" "${proof_database}"
  compose exec -T "${service}" \
    pg_restore \
      --username="${user}" \
      --dbname="${proof_database}" \
      --no-owner \
      --no-acl < "${dump}"
  local table_count
  table_count="$(compose exec -T "${service}" \
    psql \
      --username="${user}" \
      --dbname="${proof_database}" \
      --tuples-only \
      --no-align \
      --command="SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema');" \
    | tr -d '[:space:]')"
  if [[ -z "${table_count}" || "${table_count}" -le 0 ]]; then
    echo "Restored database ${proof_database} contains no owner tables." >&2
    exit 1
  fi
  compose exec -T "${service}" \
    dropdb --username="${user}" "${proof_database}"
}

restore_database catalog-db catalog_owner catalog
restore_database ingestion-db ingestion_owner ingestion
restore_database query-db query_owner query
restore_database analytics-db analytics_owner analytics
restore_database promotion-db promotion_owner promotion

OBJECT_STORAGE_CONTAINER="$(compose ps -q object-storage)"
if [[ -z "${OBJECT_STORAGE_CONTAINER}" ]]; then
  echo "Object-storage container is not running." >&2
  exit 1
fi
OBJECT_STORAGE_IMAGE="$(docker inspect --format '{{.Config.Image}}' "${OBJECT_STORAGE_CONTAINER}")"
PROOF_CONTAINER="aggregator-object-storage-restore-proof"
docker rm -f "${PROOF_CONTAINER}" >/dev/null 2>&1 || true
docker run -d \
  --name "${PROOF_CONTAINER}" \
  --publish 127.0.0.1:19000:9000 \
  --env MINIO_ROOT_USER="${OBJECT_STORAGE_ACCESS_KEY:-aggregator}" \
  --env MINIO_ROOT_PASSWORD="${OBJECT_STORAGE_SECRET_KEY:-aggregator-local-secret-key}" \
  --volume "${BACKUP_DIR}/object-storage-data:/data:ro" \
  "${OBJECT_STORAGE_IMAGE}" \
  server /data >/dev/null
cleanup_object_restore() {
  docker rm -f "${PROOF_CONTAINER}" >/dev/null 2>&1 || true
}
trap cleanup_object_restore EXIT

for _ in $(seq 1 60); do
  if curl --fail --silent http://127.0.0.1:19000/minio/health/ready >/dev/null; then
    break
  fi
  sleep 1
done
curl --fail --silent http://127.0.0.1:19000/minio/health/ready >/dev/null
find "${BACKUP_DIR}/object-storage-data" -type f -print -quit | grep -q .

echo "Database and object-storage restore proof succeeded."
