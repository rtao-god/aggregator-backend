FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore src/Promotion/Promotion.Worker/Promotion.Worker.csproj
RUN dotnet publish src/Promotion/Promotion.Worker/Promotion.Worker.csproj \
    --configuration ${BUILD_CONFIGURATION} \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Promotion.Worker.dll"]
