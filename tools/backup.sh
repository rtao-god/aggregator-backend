#!/usr/bin/env sh
set -eu

output_root="${1:-artifacts/backups/$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$output_root/databases" "$output_root/objects"

backup_database() {
  service="$1"
  database="$2"
  user="$3"
  file="$output_root/databases/$database.dump"
  docker compose exec -T "$service" pg_dump \
    --format=custom \
    --no-owner \
    --no-privileges \
    --username "$user" \
    --dbname "$database" > "$file"
  test -s "$file"
}

backup_database catalog-db catalog catalog_owner
backup_database ingestion-db ingestion ingestion_owner
backup_database query-db query query_owner
backup_database analytics-db analytics analytics_owner
backup_database promotion-db promotion promotion_owner

docker compose exec -T object-storage sh -ec \
  'tar -C /data -cf - .' > "$output_root/objects/object-storage.tar"
test -s "$output_root/objects/object-storage.tar"

(
  cd "$output_root"
  find databases objects -type f -print0 \
    | sort -z \
    | xargs -0 sha256sum > SHA256SUMS
  sha256sum -c SHA256SUMS
)

printf '%s\n' "$output_root"
