# Node gate

A **trusted, co-deployed** MeshWeaver participant that executes `javascript` / `typescript` Code
nodes. It runs as a **sidecar in the portal pod** and connects to the portal's trusted loopback gRPC
endpoint (`http://127.0.0.1:{Grpc:TrustedPort}`, default `8082`). Reachability across the shared pod
network namespace **is** the authentication — no API token, nothing to rotate. It is the Node
counterpart of the in-process Roslyn kernel: a Code node whose `Language` is `javascript`/`typescript`
is routed to the gate (address `node/node-kernel`, see `CodeNodeType.ResolveKernelAddress`), executed,
and its result written back onto the run's Activity node — surfacing exactly like a C# run. Runs
execute under the *requesting user's* identity (the gate echoes the delivery's `AccessContext`).

The kernel runs the snippet in a `vm` sandbox with REPL semantics (`console.log` captured; a trailing
bare expression is the return value; `Inputs` exposes caller parameters; TypeScript is transpiled
first). Full SDK + worker: `clients/typescript`. Trust model: `GrpcOptions.TrustedPort`.

## Build

The image compiles the TypeScript SDK (`clients/typescript`) and packs the **one canonical** proto
(`src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto`), so build from the **repo root**:

```bash
docker build -f deploy/node-gate/Dockerfile -t <registry>/meshweaver/node-gate:<tag> .
docker push <registry>/meshweaver/node-gate:<tag>
```

## Enable

Language gates are **feature-flagged** under `grpc.gates`: every language is **included by default**
(`enabled: true`), but a gate only RUNS once its image is supplied. Point the `node` gate at the
pushed image:

```yaml
grpc:
  enabled: true          # the trusted endpoint the gate connects to
  gates:
    node:
      enabled: true       # default; set false to opt javascript/typescript out
      image: <registry>/meshweaver/node-gate:<tag>
      address: node/node-kernel
```

`MESH_GRPC_URL` and `MESH_GATE_ADDRESS` (the container's env) override the endpoint and address; the
defaults match `deploy/helm` `values.grpc`. The Python gate is the sibling — see `deploy/python-gate`.
