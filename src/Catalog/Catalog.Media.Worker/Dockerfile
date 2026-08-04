FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore src/Catalog/Catalog.Media.Worker/Catalog.Media.Worker.csproj
RUN dotnet publish src/Catalog/Catalog.Media.Worker/Catalog.Media.Worker.csproj \
    --configuration ${BUILD_CONFIGURATION} \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends imagemagick \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Catalog.Media.Worker.dll"]
