# meshweaver (Python)

Connect a Python process to the MeshWeaver mesh over gRPC and use mesh features natively. This is the
foreign-language counterpart of the MAUI / Blazor-WASM participant: the process attaches at its own
`py/<uuid>` address, speaks the same `IMessageDelivery` envelopes the SignalR transport carries — only
framed over a gRPC bidirectional stream instead of SignalR.

The split, mirroring the C# side:

| Layer | C# | Python |
|---|---|---|
| Transport (participant connection) | `MeshWeaver.Connection.SignalR` | `meshweaver.connection.MeshConnection` |
| Operations (mesh features) | `MeshWeaver.AI.MeshOperations` | `meshweaver.mesh.Mesh` |
| Code execution (the kernel) | Roslyn `KernelExecutor` (C#) | `meshweaver.worker.CodeWorker` (Python) |

## Setup

```bash
pip install -e ".[dev]"
scripts/gen_proto.sh          # generate gRPC stubs from ../../src/MeshWeaver.Hosting.Grpc/Protos/mesh.proto
```

The proto lives with the C# server — there is **one** contract, no copies.

## Use

```python
import asyncio
import meshweaver as mw

async def main():
    async with await mw.Mesh.connect("https://atioz.meshweaver.cloud", token="mw_...") as mesh:
        stories = await mesh.search("nodeType:Story namespace:ACME")   # mesh -> python
        # ... native Python does the work (pandas, numpy, your model) ...
        await mesh.patch("ACME/Stories/42", {"content": {"processed": True}})  # python -> mesh
        async for node in mesh.watch("ACME/Backlog"):                  # live stream
            handle(node)

asyncio.run(main())
```

## Run Python Code nodes (the kernel worker)

The mesh's in-process kernel runs only C# (Roslyn). A Code node whose `Language == "python"` is routed
over the bridge to a connected **Python worker** — this package is that worker. It executes the submitted
script and writes the result onto the run's Activity node, so Python runs surface exactly like C# ones.

```bash
python -m meshweaver.worker --url https://atioz.meshweaver.cloud --token mw_… --address py/python-kernel
```

`execute_python(code, inputs)` is the pure execution core (captures stdout + the trailing expression's
value, REPL-style; exposes `Inputs`); `CodeWorker` is the thin mesh shell around it. Full architecture +
how to author a Python Code node: `Doc/Architecture/PythonCodeNodes`.

## Examples

Each is a runnable module with a documented mesh pattern behind it (`meshweaver/examples/`):

| Module | Pattern | Doc page |
|---|---|---|
| `pandas_node` | a stateful **backend participant**: loads a CSV file kept in mesh content into a live `pandas.DataFrame` and renders it back through the hub as a real `DataGridControl` for the C# GUI | `Doc/DataMesh/PythonPandasNode` |
| `finetune` | **mesh-orchestrated batch work**: distill the docs into a training dataset kept in content, LoRA-fine-tune an LLM on it, stream progress back onto a run node | `Doc/DataMesh/PythonFineTuning` |
| `standalone_hub` | a **complete hub in Python**: own address, handlers by message type, owned state; loads a namespace from the mesh, serves statistics, saves its report back | `Doc/DataMesh/PythonStandaloneHub` |

All three run self-contained (`--demo`, or fake-driven tests) without a mesh; point them at a portal
with `--url`/`--token` to go live.

## Status

**Confirmed live against a running portal (2026-07-03).**

- **Transport / correlation / stream-demux** — complete and protocol-faithful (mirrors
  `MeshGrpcService` + the SignalR client). Bidi `Open` + connect/ack + request/response correlation
  confirmed live; the envelope `$type` is ``MessageDelivery`1[RawJson]`` (pinned from the
  `MeshGrpcTransportTest` capture).
- **Operations** (`mesh.py`) — two transports, both confirmed live:
  - **REST** `POST {portal}/api/mesh/<verb>` (the portal's transport-mirror of the MCP tools):
    `get` / `search` / `query_nodes` / `create` / `update` / `patch` / `delete` / `move` / `copy`.
  - **gRPC sync protocol** for `watch(path)`: `SubscribeRequest(streamId, MeshNodeReference)` →
    `DataChangedEvent` (`Full` = whole node, `Patch` = **RFC 6902 JSON-Patch op array**, deduped by
    stream `version` — frames can arrive duplicated) → `UnsubscribeRequest` on exit. A REST patch
    was observed live on a gRPC watch.
- **Custom participant protocols** (e.g. the pandas node's `PandasCommand`) route by target address
  with no server-side registration — requires the server's `RawJsonPassThrough` proxy-hub policy
  (`GrpcConnectionRegistry`), shipped alongside this SDK.

The bidi gRPC connection needs HTTP/2 end-to-end; the deployment routes `meshweaver.v1.Mesh/Open`
natively at the ordinary portal URL (helm `values.grpc` — live on all hosted portals). For a
self-signed local portal pass the CA via `connect(..., root_certificates=...)`.

Security — two participant classes:

* **External participants** authenticate with an API token (`Authorization: Bearer mw_…` in gRPC
  call metadata); the server validates it and re-stamps every write with the validated identity.
  A forged client-side identity is never trusted.
* **Trusted gates** — services that ship *in the same deployment* as the portal (the co-located
  node / bun / python gates) — connect to the trusted loopback endpoint (`http://127.0.0.1:8082`
  inside the portal's pod). Reachability is the authentication (only same-pod containers share
  loopback): no token, nothing to rotate. A trusted gate may carry the requesting user's
  `accessContext` through on its deliveries (`respond` echoes it automatically), so work done on a
  user's behalf writes under that user's identity — like the in-process C# kernel.
