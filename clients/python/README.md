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

## Status

- **Transport / correlation / stream-demux** — complete and protocol-faithful (mirrors
  `MeshGrpcService` + the SignalR client).
- **Envelope wire-shape** (`envelope.py`) and **operation request types** (`mesh.py`, marked `# WIRE:`)
  — pinned to the mesh's `IMessageDelivery` JSON. Confirm the exact `$type` token + property casing
  against a sample captured from the C# round-trip test, then the `# WIRE:` spots are a one-line fix
  each. Everything beneath them already works.

Security: the API token travels in gRPC call metadata; the server validates it and stamps every write
with your identity. A forged client-side identity is never trusted.
