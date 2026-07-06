# The Node gate — a TRUSTED, co-deployed MeshWeaver participant that executes `javascript` /
# `typescript` Code nodes.
#
# It runs as a SIDECAR inside the portal pod and connects to the portal's trusted loopback gRPC
# endpoint (http://127.0.0.1:{Grpc:TrustedPort}). Reachability across the shared pod network
# namespace IS the authentication — no API token, nothing to rotate. This is the Node counterpart of
# the in-process Roslyn kernel: a Code node whose `Language` is `javascript`/`typescript` is routed to
# this gate (address `node/node-kernel`, see CodeNodeType.ResolveKernelAddress), executed, and its
# result written back onto the run's Activity node.
#
# Build from the REPO ROOT (the SDK package AND the one canonical proto both live there):
#   docker build -f deploy/node-gate/Dockerfile -t meshweaver/node-gate:local .

# ---- build stage: install deps + compile the TypeScript SDK to dist/ ------------------------------
FROM node:22-slim AS build
WORKDIR /app
COPY clients/typescript/package.json ./
RUN npm install --no-audit --no-fund
COPY clients/typescript/tsconfig.json ./
COPY clients/typescript/src ./src
RUN npm run build && npm prune --omit=dev

# ---- runtime stage: node + dist + prod deps + the one canonical proto -----------------------------
FROM node:22-slim
RUN useradd --create-home --uid 10001 gate
WORKDIR /app
COPY --from=build /app/package.json ./
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
# The canonical proto is loaded at runtime by @grpc/proto-loader (connection.ts reads MESHWEAVER_PROTO).
COPY src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto /app/mesh.proto
# Numeric uid (not the name) so Kubernetes' runAsNonRoot check can verify non-root.
USER 10001

# The trusted endpoint + the well-known address the .NET side routes js/ts submissions to; both
# overridable, defaults match deploy/helm values.grpc. NODE_ENV=production; no buffering so the
# worker's connect line + errors reach the pod log immediately.
ENV MESH_GRPC_URL=http://127.0.0.1:8082 \
    MESH_GATE_ADDRESS=node/node-kernel \
    MESHWEAVER_PROTO=/app/mesh.proto \
    NODE_ENV=production

# The node kernel serves SubmitCodeRequest against the trusted endpoint with NO token (trust is the
# loopback). --reconnect: the gate outlives portal restarts and tolerates starting before the portal
# has bound the endpoint. exec so node is the signalled process (clean SIGTERM; the worker's signal
# handler stops the reconnect loop).
CMD ["/bin/sh", "-c", "exec node dist/worker.js --url \"$MESH_GRPC_URL\" --address \"$MESH_GATE_ADDRESS\" --reconnect"]
