# syntax=docker/dockerfile:1
# Build context: src/backend.  Image name: support-backend.
#   docker build -t support-backend src/backend

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer cache): only manifests and project files.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/SupportChat.Core/SupportChat.Core.csproj src/SupportChat.Core/
COPY src/SupportChat.Agents/SupportChat.Agents.csproj src/SupportChat.Agents/
COPY src/SupportChat.Infrastructure/SupportChat.Infrastructure.csproj src/SupportChat.Infrastructure/
COPY src/SupportChat.Api/SupportChat.Api.csproj src/SupportChat.Api/
RUN dotnet restore src/SupportChat.Api/SupportChat.Api.csproj

COPY src/ src/
RUN dotnet publish src/SupportChat.Api/SupportChat.Api.csproj \
      -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Non-root: the aspnet image ships an unprivileged "app" user (APP_UID).
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "SupportChat.Api.dll"]
