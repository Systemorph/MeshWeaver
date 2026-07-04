# The Python gate — a TRUSTED, co-deployed MeshWeaver participant that executes `python` Code nodes.
#
# It runs as a SIDECAR inside the portal pod and connects to the portal's trusted loopback gRPC
# endpoint (http://127.0.0.1:{Grpc:TrustedPort}). Reachability across the shared pod network
# namespace IS the authentication — no API token, nothing to rotate. This is the Python counterpart
# of the in-process Roslyn kernel: a Code node whose `Language == "python"` is routed to this gate
# (address `py/python-kernel`), executed, and its result written back onto the run's Activity node.
#
# Build from the REPO ROOT (the SDK package AND the one canonical proto both live there):
#   docker build -f deploy/python-gate/Dockerfile -t meshweaver/python-gate:local .

# ---- build stage: generate the gRPC stubs from mesh.proto, then build a wheel ---------------------
FROM python:3.12-slim AS build
WORKDIR /w
# The SDK package + the ONE canonical proto, kept at its repo-relative path so gen_proto.sh resolves it.
COPY clients/python /w/clients/python
COPY src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto /w/src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto
# Generate the stubs INTO the source tree BEFORE building the wheel, so meshweaver._generated is packaged.
RUN pip install --no-cache-dir "grpcio-tools>=1.68" \
    && PYTHON=python bash /w/clients/python/scripts/gen_proto.sh \
    && pip wheel --no-cache-dir --wheel-dir /wheels /w/clients/python

# ---- runtime stage: just the wheel + its runtime deps (grpcio, protobuf) — no build tooling -------
FROM python:3.12-slim
RUN useradd --create-home --uid 10001 gate
COPY --from=build /wheels /wheels
RUN pip install --no-cache-dir /wheels/*.whl && rm -rf /wheels
# Numeric uid (not the name) so Kubernetes' runAsNonRoot check can verify non-root.
USER 10001

# The trusted endpoint and the well-known kernel address the .NET side routes `python` submissions to
# (CodeNodeType). Both overridable; the defaults match deploy/helm values.grpc.
# PYTHONUNBUFFERED so the worker's stdout (connect line, errors) reaches the pod log immediately.
ENV MESH_GRPC_URL=http://127.0.0.1:8082 \
    MESH_GATE_ADDRESS=py/python-kernel \
    PYTHONUNBUFFERED=1

# `meshweaver.worker` serves SubmitCodeRequest against the trusted endpoint with NO token (the trust is
# the loopback). --reconnect: the gate outlives portal restarts (deploys / liveness) and tolerates
# starting before the portal has bound the endpoint — no crash-restart. exec so python is the signalled
# process (clean SIGTERM on pod stop; the worker's signal handler stops the reconnect loop).
CMD ["/bin/sh", "-c", "exec python -m meshweaver.worker --url \"$MESH_GRPC_URL\" --address \"$MESH_GATE_ADDRESS\" --reconnect"]
