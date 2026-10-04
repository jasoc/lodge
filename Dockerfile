# Lodge — one image, one container: the SPA is built here and served as static files by
# Lodge.Server itself, and Badgie.Migrator is bundled alongside it so the server can
# migrate its own schema. Lives at the repo root, next to its build context (it needs ui/,
# server/src/ and migrations/ all at once; .dockerignore keeps the rest out). inventory/ and
# schemas/ (the SSOT) are NOT copied in: they're bind-mounted at runtime (see
# docker-compose.prod.yml) so the source of truth stays external to the image.

# ---- SPA build ----
FROM node:22.23.1-alpine AS spa-build
WORKDIR /spa
COPY ui/package.json ui/package-lock.json ./
RUN npm ci
COPY ui/ .
RUN npm run build

# ---- server build ----
# SDK image pinned to the exact version in /global.json, not a floating tag — a newer
# feature-band SDK resolved a broken analyzer package graph across separate
# restore/publish layers, so this publishes in one atomic step instead.
FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS server-build
WORKDIR /src
COPY server/src/ src/
RUN dotnet publish src/Lodge.Server/Lodge.Server.csproj -c Release -o /app/publish
# Bundled next to the app (not the global tools dir) so the runtime image — which has no
# SDK — can still run it; MigrationRunner looks for it at <app>/tools/ first.
RUN dotnet tool install --tool-path /app/publish/tools Badgie.Migrator

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0.0 AS runtime
WORKDIR /app
# docker.io + docker-buildx: client-only usage — no daemon ever runs in here.
# The container executor's Docker runtime talks to the HOST's daemon through the socket docker-compose.prod.yml
# mounts at /var/run/docker.sock (or whatever DOCKER_HOST points at), pulling images and
# building inventory playbook folders as sibling containers. buildx makes `docker build`
# use BuildKit instead of the deprecated legacy builder (and enables --build-context).
# TODO: install pass-cli (Proton Pass CLI) here too, for PassCliSecretProvider
# (Secrets:Provider=PassCli) — its distribution/install method isn't verified from
# anything in this repo (the homelab scripts assume it's already on PATH), so no install
# command is guessed here; fill this in against the real pass-cli release artifact.
RUN apt-get update \
    && apt-get install -y --no-install-recommends docker.io docker-buildx \
    && rm -rf /var/lib/apt/lists/*
COPY --from=server-build /app/publish ./
COPY --from=spa-build /spa/dist/spa/browser ./wwwroot
# Lives under /repo (not /app) so MigrationRunner finds it the same way it finds
# inventory/schemas — relative to GitSnapshot__RepoRoot, one resolution rule everywhere.
COPY migrations/ /repo/migrations/

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV GitSnapshot__RepoRoot=/repo
ENV HttpExecutor__LogDirectory=/repo/data/http-run-logs
ENV DockerExecutor__LogDirectory=/repo/data/docker-runbook-logs

ENTRYPOINT ["dotnet", "Lodge.Server.dll"]
