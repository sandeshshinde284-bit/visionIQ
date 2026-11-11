# syntax=docker/dockerfile:1
# LRN-12 ADO Service Hook host. Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig ./
COPY src/VisionIQ.Ingestor/VisionIQ.Ingestor.csproj src/VisionIQ.Ingestor/
COPY src/VisionIQ.Api/VisionIQ.Api.csproj src/VisionIQ.Api/
COPY src/VisionIQ.Ingestor.Cli/VisionIQ.Ingestor.Cli.csproj src/VisionIQ.Ingestor.Cli/
RUN dotnet restore src/VisionIQ.Ingestor.Cli/VisionIQ.Ingestor.Cli.csproj
COPY src/VisionIQ.Ingestor/ src/VisionIQ.Ingestor/
COPY src/VisionIQ.Api/ src/VisionIQ.Api/
COPY src/VisionIQ.Ingestor.Cli/ src/VisionIQ.Ingestor.Cli/
RUN dotnet publish src/VisionIQ.Ingestor.Cli/VisionIQ.Ingestor.Cli.csproj -c Release \
    -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Cloud Run supplies PORT=8080. WebhookHost binds 0.0.0.0 on that port.
ENV PORT=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "VisionIQ.Ingestor.Cli.dll", "serve-webhooks"]
