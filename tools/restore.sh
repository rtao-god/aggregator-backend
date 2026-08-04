#!/usr/bin/env sh
set -eu

backup_root="${1:?usage: tools/restore.sh <backup-directory>}"
(
  cd "$backup_root"
  sha256sum -c SHA256SUMS
)

restore_database() {
  service="$1"
  database="$2"
  user="$3"
  file="$backup_root/databases/$database.dump"
  test -s "$file"
  docker compose exec -T "$service" dropdb \
    --if-exists \
    --force \
    --username "$user" "$database"
  docker compose exec -T "$service" createdb \
    --username "$user" "$database"
  docker compose exec -T "$service" pg_restore \
    --exit-on-error \
    --clean \
    --if-exists \
    --no-owner \
    --no-privileges \
    --username "$user" \
    --dbname "$database" < "$file"
}

restore_database catalog-db catalog catalog_owner
restore_database ingestion-db ingestion ingestion_owner
restore_database query-db query query_owner
restore_database analytics-db analytics analytics_owner
restore_database promotion-db promotion promotion_owner

object_archive="$backup_root/objects/object-storage.tar"
test -s "$object_archive"
docker compose exec -T object-storage sh -ec 'rm -rf /data/*'
docker compose exec -T object-storage sh -ec 'tar -C /data -xf -' < "$object_archive"

printf 'restore completed from %s\n' "$backup_root"
