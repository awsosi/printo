# Printo server images.
#
# One file, one dependency install, three runtime targets - `api`, `web` and `worker` - built
# with `docker compose --profile prod build` or `docker build --target api .`. A monorepo whose
# three services share a lockfile and two workspace packages is best served by one build that
# resolves that graph once; three Dockerfiles would each re-resolve it and drift apart.
#
# The services run through `tsx` rather than compiled JavaScript, deliberately. The workspace
# packages export TypeScript sources directly (`"exports": "./src/index.ts"`), which is what lets
# the routing engine be the single source of truth for the profiles the C# agent embeds. Emitting
# JavaScript would mean changing every package's entry point and re-pointing the profile exporter
# and the conformance tooling at build output. That is a refactor with real risk and no
# operational benefit here: startup cost is a few hundred milliseconds, once, per container.

FROM node:22-bookworm-slim AS deps

WORKDIR /app

# Only the manifests first, so a source-only change reuses the install layer.
COPY package.json package-lock.json ./
COPY apps/api/package.json apps/api/
COPY apps/web/package.json apps/web/
COPY apps/worker/package.json apps/worker/
COPY packages/shared/package.json packages/shared/
COPY packages/routing-engine/package.json packages/routing-engine/

# `npm ci` rather than `npm install`: the lockfile is the contract, and a production image that
# silently resolved a different tree than CI tested would defeat the point of having one.
RUN npm ci --include=dev

FROM node:22-bookworm-slim AS source

WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY package.json package-lock.json tsconfig.base.json ./
COPY apps ./apps
COPY packages ./packages
COPY profiles ./profiles

# The API resolves migrations relative to the repository root at runtime, so they are part of
# the image rather than something mounted alongside it: a container that cannot migrate is a
# container that cannot start, and finding that out in production is too late.
COPY infra/migrations ./infra/migrations

# Typecheck in the image, so a build that would fail at runtime fails here instead.
RUN npm run typecheck

# ---------------------------------------------------------------------------------------------

FROM node:22-bookworm-slim AS runtime-base

ENV NODE_ENV=production
WORKDIR /app

# Unprivileged: `node` exists in the base image with uid 1000.
COPY --from=source --chown=node:node /app /app
USER node

# ---------------------------------------------------------------------------------------------

FROM runtime-base AS api

EXPOSE 4000
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=6 \
  CMD node -e "fetch('http://127.0.0.1:'+(process.env.PORT||4000)+'/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

# Migrations run before the server, in the API image, because the API owns the schema. Doing it
# in an entrypoint rather than a separate job keeps `docker compose up -d` a single command.
CMD ["sh", "-c", "node node_modules/tsx/dist/cli.mjs apps/api/src/db/migrate.ts && node node_modules/tsx/dist/cli.mjs apps/api/src/server.ts"]

# ---------------------------------------------------------------------------------------------

FROM runtime-base AS web

EXPOSE 3000
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=6 \
  CMD node -e "fetch('http://127.0.0.1:'+(process.env.PORT||3000)+'/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

CMD ["node", "node_modules/tsx/dist/cli.mjs", "apps/web/src/server.ts"]

# ---------------------------------------------------------------------------------------------

FROM runtime-base AS worker

# The worker mounts SMB shares to collect scans, so it needs a client. Installed as root, before
# dropping back to `node`.
USER root
RUN apt-get update \
  && apt-get install -y --no-install-recommends smbclient \
  && rm -rf /var/lib/apt/lists/*
USER node

EXPOSE 5000
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=6 \
  CMD node -e "fetch('http://127.0.0.1:'+(process.env.PORT||5000)+'/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

CMD ["node", "node_modules/tsx/dist/cli.mjs", "apps/worker/src/server.ts"]
