# ============================================================
# Dockerfile — AsistenteJuridico.API
# Multi-stage build: build + runtime
# ============================================================

# ── Etapa 1: Build ───────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar archivos de proyecto primero para aprovechar caché de capas
COPY ["src/AsistenteJuridico.API/AsistenteJuridico.API.csproj", "src/AsistenteJuridico.API/"]
COPY ["src/AsistenteJuridico.Application/AsistenteJuridico.Application.csproj", "src/AsistenteJuridico.Application/"]
COPY ["src/AsistenteJuridico.Domain/AsistenteJuridico.Domain.csproj", "src/AsistenteJuridico.Domain/"]
COPY ["src/AsistenteJuridico.Infrastructure/AsistenteJuridico.Infrastructure.csproj", "src/AsistenteJuridico.Infrastructure/"]

# Restaurar dependencias
RUN dotnet restore "src/AsistenteJuridico.API/AsistenteJuridico.API.csproj"

# Copiar código fuente completo
COPY . .

# Publicar en modo Release
WORKDIR "/src/src/AsistenteJuridico.API"
RUN dotnet publish "AsistenteJuridico.API.csproj" -c Release -o /app/publish --no-restore

# ── Etapa 2: Runtime ─────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Usuario no-root por seguridad
RUN addgroup --system --gid 1001 appgroup && \
    adduser --system --uid 1001 --ingroup appgroup appuser

# Copiar artefactos publicados
COPY --from=build /app/publish .

# Crear directorio de logs con permisos correctos
RUN mkdir -p /app/logs && chown -R appuser:appgroup /app

USER appuser

# Puerto de escucha
EXPOSE 8080

# Variables de entorno
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "AsistenteJuridico.API.dll"]
