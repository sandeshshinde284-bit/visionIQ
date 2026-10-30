# syntax=docker/dockerfile:1
# Build context is the repository root (see docker-compose.yml): docker compose build api

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached until the project file changes.
COPY global.json Directory.Build.props .editorconfig ./
COPY src/VisionIQ.Api/VisionIQ.Api.csproj src/VisionIQ.Api/
RUN dotnet restore src/VisionIQ.Api/VisionIQ.Api.csproj

COPY src/VisionIQ.Api/ src/VisionIQ.Api/
RUN dotnet publish src/VisionIQ.Api/VisionIQ.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# curl is only used by the container health check.
# hadolint ignore=DL3008
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Listen on all interfaces inside the container. Cloud Run overrides the port through PORT.
ENV ASPNETCORE_URLS=http://+:5100
EXPOSE 5100

# Run as the non-root user provided by the base image (APP_UID is defined by the .NET 8 images).
# hadolint ignore=DL3066
USER $APP_UID

HEALTHCHECK --interval=15s --timeout=3s --start-period=15s --retries=3 \
    CMD ["sh", "-c", "curl -fsS http://127.0.0.1:${PORT:-5100}/health"]

ENTRYPOINT ["dotnet", "VisionIQ.Api.dll"]
