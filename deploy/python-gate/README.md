# Python gate

A **trusted, co-deployed** MeshWeaver participant that executes `python` Code nodes. It runs as a
**sidecar in the portal pod** and connects to the portal's trusted loopback gRPC endpoint
(`http://127.0.0.1:{Grpc:TrustedPort}`, default `8082`). Reachability across the shared pod network
namespace **is** the authentication — no API token, nothing to rotate. It is the Python counterpart of
the in-process Roslyn kernel: a Code node whose `Language == "python"` is routed to the gate (address
`py/python-kernel`), executed, and its result written back onto the run's Activity node — surfacing
exactly like a C# run. Runs execute under the *requesting user's* identity (the gate echoes the
delivery's `AccessContext`).

Full architecture: `Doc/Architecture/PythonCodeNodes`. The trust model: `Doc/DataMesh/PythonStandaloneHub`
(the trusted-gate note) and `GrpcOptions.TrustedPort`.

## Build

The image packs the Python SDK (`clients/python`) and the **one canonical** proto
(`src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto`), so build from the **repo root**:

```bash
docker build -f deploy/python-gate/Dockerfile -t <registry>/meshweaver/python-gate:<tag> .
docker push <registry>/meshweaver/python-gate:<tag>
```

## Enable

Language gates are **feature-flagged** under `grpc.gates`: every language is **included by default**
(`enabled: true`), but a gate only RUNS once its image is supplied (empty image ⇒ no sidecar, so a
bare install never crash-loops). Point the `python` gate at the pushed image in a Helm overlay /
`--set`:

```yaml
grpc:
  enabled: true          # the trusted endpoint the gate connects to
  gates:
    python:
      enabled: true       # default; set false to opt python out
      image: <registry>/meshweaver/python-gate:<tag>
      address: py/python-kernel
```

`MESH_GRPC_URL` and `MESH_GATE_ADDRESS` (the container's env) override the endpoint and address; the
defaults match `deploy/helm` `values.grpc`. The Node gate (`javascript`/`typescript`) is the sibling —
see `deploy/node-gate`.
