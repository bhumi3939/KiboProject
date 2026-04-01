# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Disable NuGet fallback package folder to avoid Windows-specific path issues
ENV NUGET_FALLBACK_PACKAGES=""

# Copy project files first for layer-cached restore
COPY src/CommerceHub.API/CommerceHub.API.csproj ./CommerceHub.API/
RUN dotnet restore ./CommerceHub.API/CommerceHub.API.csproj --no-cache

# Copy full source and publish
COPY src/CommerceHub.API/ ./CommerceHub.API/
WORKDIR /src/CommerceHub.API
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root user for security
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

# Docker environment activates appsettings.Docker.json overrides
ENV ASPNETCORE_ENVIRONMENT=Docker
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "CommerceHub.API.dll"]
