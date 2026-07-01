# syntax=docker/dockerfile:1
# MeshWeaver portal-next — Next.js standalone runner.
#
# Build CONTEXT is the clients/ directory (the app aliases @meshweaver/react and
# @meshweaver/client-web to their SOURCE trees in ../react and ../grpc-web):
#
#   docker build -f portal-next/Dockerfile -t meshweaver/portal-next:dev clients/
#
# Dockerfile.dockerignore (BuildKit, sits next to this file) keeps node_modules/.next/dist
# out of the context; all deps install fresh from portal-next/package-lock.json — the
# webpack resolve.modules override makes the sibling sources resolve every bare import
# from portal-next/node_modules, so ../react and ../grpc-web need no install of their own.

FROM node:22-alpine AS build
WORKDIR /workspace
COPY react/ react/
COPY grpc-web/ grpc-web/
COPY portal-next/ portal-next/
WORKDIR /workspace/portal-next
RUN npm ci --no-audit --no-fund
RUN npm run build

FROM node:22-alpine AS runner
ENV NODE_ENV=production \
    PORT=3000 \
    HOSTNAME=0.0.0.0
WORKDIR /app
# outputFileTracingRoot is the monorepo root (clients/), so the standalone bundle nests
# the app under portal-next/.
COPY --from=build /workspace/portal-next/.next/standalone ./
COPY --from=build /workspace/portal-next/.next/static ./portal-next/.next/static
USER node
EXPOSE 3000
# PORTAL_ORIGIN (optional): the portal origin for server-side token mint + snapshot fetch.
# Defaults to the incoming request's forwarded host — correct when the same ingress host
# fronts both the portal and this app; set it to the in-cluster portal Service URL to skip
# the ingress round-trip (e.g. http://memex-portal-service:8080).
CMD ["node", "portal-next/server.js"]
