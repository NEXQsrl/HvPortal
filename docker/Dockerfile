# HvPortal gateway (web console + agent channel) for Linux containers.
# Build from the repository root:   docker build --provenance=false --sbom=false --platform linux/amd64 -f docker/Dockerfile -t hvportal-gateway:0.9.0 .
# The Hyper-V agents stay Windows services on the hosts; they connect to this container on the agent port (mTLS).

# ---------------------------------------------------------------- web UI (React/Vite)
FROM node:22-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# vite.config.ts writes to ../src/HvPortal.Gateway/wwwroot
RUN mkdir -p /src/src/HvPortal.Gateway && npx vite build

# ---------------------------------------------------------------- gateway + Windows agent package
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
# global.json is not copied: it pins the Windows development SDK; the image SDK is used instead.
COPY Directory.Build.props HvPortal.slnx ./
COPY src/ src/
COPY installer/ installer/
COPY --from=web /src/src/HvPortal.Gateway/wwwroot src/HvPortal.Gateway/wwwroot
RUN dotnet publish src/HvPortal.Gateway/HvPortal.Gateway.csproj -c Release -o /out/gateway -p:DebugType=none
# Agent package served by the portal (Guide > Add host): self-contained win-x64, cross-published.
# Authenticode cannot be verified on Linux: the portal shows it as unsigned (sign it on Windows if required).
RUN pwsh -NoProfile -File installer/New-AgentPackage.ps1 -OutputDirectory /out/packages

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="HvPortal gateway" \
      org.opencontainers.image.description="Browser-based Hyper-V management: web console, REST API, agent channel (mTLS)" \
      org.opencontainers.image.vendor="NEXQ S.r.l." \
      org.opencontainers.image.source="https://github.com/NEXQsrl/HvPortal" \
      org.opencontainers.image.licenses="LicenseRef-PolyForm-Shield-1.0.0"
ENV HVP_DATA_DIR=/data \
    Gateway__BindAddress=0.0.0.0 \
    Gateway__UiPort=8443 \
    Gateway__AgentPort=8444 \
    Gateway__PackagesDirectory=/app/packages \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_ENVIRONMENT=Production
WORKDIR /app
COPY --from=build /out/gateway/ /app/
COPY --from=build /out/packages/ /app/packages/
# License terms and Required Notice (PolyForm Shield 1.0.0): whoever gets a copy of the image gets them too.
COPY LICENSE NOTICE /app/
# Non-root ("app", uid 1654, provided by the base image). /data must be writable by it.
RUN mkdir -p /data && chown -R app:app /data && chmod 700 /data
USER app
VOLUME ["/data"]
EXPOSE 8443 8444
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 CMD ["dotnet", "/app/HvPortal.Gateway.dll", "admin", "health"]
ENTRYPOINT ["dotnet", "/app/HvPortal.Gateway.dll"]
