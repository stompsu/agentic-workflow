# syntax=docker/dockerfile:1
# Build context: src/backend.  Image name: agentic-workflow-backend.
#   docker build -t agentic-workflow-backend src/backend

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer cache): only manifests and project files.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/AgenticWorkflow.Core/AgenticWorkflow.Core.csproj src/AgenticWorkflow.Core/
COPY src/AgenticWorkflow.Agents/AgenticWorkflow.Agents.csproj src/AgenticWorkflow.Agents/
COPY src/AgenticWorkflow.Infrastructure/AgenticWorkflow.Infrastructure.csproj src/AgenticWorkflow.Infrastructure/
COPY src/AgenticWorkflow.Api/AgenticWorkflow.Api.csproj src/AgenticWorkflow.Api/
RUN dotnet restore src/AgenticWorkflow.Api/AgenticWorkflow.Api.csproj

COPY src/ src/
RUN dotnet publish src/AgenticWorkflow.Api/AgenticWorkflow.Api.csproj \
      -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Non-root: the aspnet image ships an unprivileged "app" user (APP_UID).
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgenticWorkflow.Api.dll"]
