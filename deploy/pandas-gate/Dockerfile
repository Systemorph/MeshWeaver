# The Pandas gate — a TRUSTED, co-deployed MeshWeaver participant that holds a live pandas DataFrame
# at address `py/pandas`. It is the stateful backend for the PandasExplorer example
# (Doc/DataMesh/PythonPandasNode): the .NET frontend posts `PandasCommand` messages, this participant
# reads a CSV content node over the mesh, does the pandas work, and returns a real `DataGridControl`.
#
# Like the python/node gates it runs as a SIDECAR in the portal pod and connects to the trusted
# loopback endpoint (http://127.0.0.1:{Grpc:TrustedPort}) — reachability IS the authentication, no
# token. Unlike the code gates it is NOT a Code-node kernel; it is a long-lived stateful participant.
#
# Build from the REPO ROOT: docker build -f deploy/pandas-gate/Dockerfile -t meshweaver/pandas-gate:local .

# ---- build stage: generate the gRPC stubs, then build the SDK wheel (incl. meshweaver.examples) ----
FROM python:3.12-slim AS build
WORKDIR /w
COPY clients/python /w/clients/python
COPY src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto /w/src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto
RUN pip install --no-cache-dir "grpcio-tools>=1.68" \
    && PYTHON=python bash /w/clients/python/scripts/gen_proto.sh \
    && pip wheel --no-cache-dir --wheel-dir /wheels /w/clients/python

# ---- runtime stage: the wheel + pandas (the participant holds a DataFrame) -------------------------
FROM python:3.12-slim
RUN useradd --create-home --uid 10001 gate
COPY --from=build /wheels /wheels
RUN pip install --no-cache-dir /wheels/*.whl "pandas>=2.0" && rm -rf /wheels
# Numeric uid so Kubernetes' runAsNonRoot check can verify non-root.
USER 10001

ENV MESH_GRPC_URL=http://127.0.0.1:8082 \
    MESH_GATE_ADDRESS=py/pandas \
    PYTHONUNBUFFERED=1

# --reconnect: the participant outlives portal restarts and tolerates starting before the portal has
# bound the endpoint. exec so python is the signalled process (clean SIGTERM).
CMD ["/bin/sh", "-c", "exec python -m meshweaver.examples.pandas_node --url \"$MESH_GRPC_URL\" --address \"$MESH_GATE_ADDRESS\" --reconnect"]
