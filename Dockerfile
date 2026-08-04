# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG PROJECT
ARG ENTRY_DLL
COPY . .
RUN test -n "$PROJECT" && test -n "$ENTRY_DLL"
RUN dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ARG ENTRY_DLL
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080 \
    APP_ENTRY_DLL=$ENTRY_DLL
RUN addgroup --system --gid 1654 app && adduser --system --uid 1654 --ingroup app app
COPY --from=build --chown=app:app /app/publish .
USER app
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_ENTRY_DLL\""]
