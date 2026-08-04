#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
API = ROOT / "src" / "Catalog" / "Catalog.Media.Api"
DEPLOY = ROOT / "deploy"
API.mkdir(parents=True, exist_ok=True)
DEPLOY.mkdir(parents=True, exist_ok=True)

(API / "Dockerfile").write_text(
    '''FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore src/Catalog/Catalog.Media.Api/Catalog.Media.Api.csproj
RUN dotnet publish src/Catalog/Catalog.Media.Api/Catalog.Media.Api.csproj \\
    --configuration ${BUILD_CONFIGURATION} \\
    --no-restore \\
    --output /app/publish \\
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Catalog.Media.Api.dll"]
''',
    encoding="utf-8",
)

(DEPLOY / "compose.catalog-media.yaml").write_text(
    '''services:
  catalog-media-api:
    build:
      context: ..
      dockerfile: src/Catalog/Catalog.Media.Api/Dockerfile
    restart: unless-stopped
    environment:
      ConnectionStrings__Catalog: ${CATALOG_APP_CONNECTION_STRING:?CATALOG_APP_CONNECTION_STRING is required}
      Authentication__Authority: ${AUTHENTICATION_AUTHORITY:?AUTHENTICATION_AUTHORITY is required}
      Authentication__RequireHttpsMetadata: ${AUTHENTICATION_REQUIRE_HTTPS_METADATA:-true}
      CatalogMedia__ObjectStorage__ServiceUrl: ${OBJECT_STORAGE_SERVICE_URL:?OBJECT_STORAGE_SERVICE_URL is required}
      CatalogMedia__ObjectStorage__Region: ${OBJECT_STORAGE_REGION:-us-east-1}
      CatalogMedia__ObjectStorage__Bucket: ${CATALOG_MEDIA_BUCKET:?CATALOG_MEDIA_BUCKET is required}
      CatalogMedia__ObjectStorage__AccessKey: ${CATALOG_MEDIA_ACCESS_KEY:?CATALOG_MEDIA_ACCESS_KEY is required}
      CatalogMedia__ObjectStorage__SecretKey: ${CATALOG_MEDIA_SECRET_KEY:?CATALOG_MEDIA_SECRET_KEY is required}
      CatalogMedia__ObjectStorage__ForcePathStyle: ${OBJECT_STORAGE_FORCE_PATH_STYLE:-true}
      OTEL_EXPORTER_OTLP_ENDPOINT: ${OTEL_EXPORTER_OTLP_ENDPOINT:-}
    networks: [backend]

  catalog-media-worker:
    build:
      context: ..
      dockerfile: src/Catalog/Catalog.Media.Worker/Dockerfile
    restart: unless-stopped
    depends_on:
      - clamav
    environment:
      ConnectionStrings__Catalog: ${CATALOG_APP_CONNECTION_STRING:?CATALOG_APP_CONNECTION_STRING is required}
      Messaging__BrokerUri: ${RABBITMQ_BROKER_URI:?RABBITMQ_BROKER_URI is required}
      Messaging__Exchange: ${RABBITMQ_EVENTS_EXCHANGE:-aggregator.events}
      CatalogMediaWorker__WorkerIdentity: ${CATALOG_MEDIA_WORKER_IDENTITY:-catalog-media-worker-local}
      CatalogMediaWorker__SystemActorId: ${CATALOG_MEDIA_SYSTEM_ACTOR_ID:?CATALOG_MEDIA_SYSTEM_ACTOR_ID is required}
      CatalogMediaWorker__ClamAvHost: clamav
      CatalogMediaWorker__ClamAvPort: 3310
      CatalogMediaWorker__MaximumAttempts: ${CATALOG_MEDIA_MAXIMUM_ATTEMPTS:-8}
      CatalogMediaWorker__LeaseDurationSeconds: ${CATALOG_MEDIA_LEASE_DURATION_SECONDS:-300}
      CatalogMediaWorker__EmptyDelayMilliseconds: ${CATALOG_MEDIA_EMPTY_DELAY_MILLISECONDS:-2000}
      CatalogMedia__ObjectStorage__ServiceUrl: ${OBJECT_STORAGE_SERVICE_URL:?OBJECT_STORAGE_SERVICE_URL is required}
      CatalogMedia__ObjectStorage__Region: ${OBJECT_STORAGE_REGION:-us-east-1}
      CatalogMedia__ObjectStorage__Bucket: ${CATALOG_MEDIA_BUCKET:?CATALOG_MEDIA_BUCKET is required}
      CatalogMedia__ObjectStorage__AccessKey: ${CATALOG_MEDIA_ACCESS_KEY:?CATALOG_MEDIA_ACCESS_KEY is required}
      CatalogMedia__ObjectStorage__SecretKey: ${CATALOG_MEDIA_SECRET_KEY:?CATALOG_MEDIA_SECRET_KEY is required}
      CatalogMedia__ObjectStorage__ForcePathStyle: ${OBJECT_STORAGE_FORCE_PATH_STYLE:-true}
      OTEL_EXPORTER_OTLP_ENDPOINT: ${OTEL_EXPORTER_OTLP_ENDPOINT:-}
    networks: [backend]

  clamav:
    image: ${CLAMAV_IMAGE:?CLAMAV_IMAGE must be an explicitly pinned image reference}
    restart: unless-stopped
    expose:
      - "3310"
    volumes:
      - clamav-signatures:/var/lib/clamav
    networks: [backend]

volumes:
  clamav-signatures:

networks:
  backend:
    external: true
    name: ${BACKEND_NETWORK_NAME:-aggregator-backend}
''',
    encoding="utf-8",
)
