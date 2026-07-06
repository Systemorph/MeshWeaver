---
Name: Python Code Nodes
Category: Architecture
Description: Code nodes with Language = python route over the mesh to a connected Python worker — the actual node, the worker command, and the execution flow.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2c-4 0-5 2-5 4v3h5v1H5c-2 0-3 1.5-3 4s1 4 3 4h2v-3c0-2 1-3 3-3h5c1.7 0 3-1.3 3-3V6c0-2-1-4-6-4z"/><path d="M12 22c4 0 5-2 5-4v-3h-5v-1h7c2 0 3-1.5 3-4"/></svg>
---

# Python Code Nodes

Here is a **runnable `python` Code node**. Press **Run** in its toolbar — the script executes on a
connected Python worker and the output attaches directly below the code:

@@SampleStatistics

That is the whole feature. What you just ran is a **Code node** whose `Language` is `python`:
`print(...)` was captured as output, the trailing bare expression became the return value (REPL
semantics), and `Inputs` carried the caller's parameters. A `csharp` node (the default) runs
**in-process** on the mesh's Roslyn kernel; a **foreign-language** node is **routed over the mesh to a
connected worker** instead — `python` → `py/python-kernel`, `javascript`/`typescript` →
`node/node-kernel` (`CodeNodeType.ResolveKernelAddress`) — the same gRPC bridge that lets any process
join the mesh as a participant (see [Foreign Language Integration](../ForeignLanguageIntegration)). The
worker runs the script and writes the result back onto the same **Activity node** every subscriber
already watches, so a python **or** js/ts run surfaces output **identically** to a C# one. With no
worker connected the run reports that in the same output pane — nothing hangs.

## Inside the node

The node above is nothing more than a `CodeConfiguration` with `Language = "python"` and
`IsExecutable = true`:

```json
{
  "id": "SampleStatistics",
  "namespace": "Doc/Architecture/PythonCodeNodes",
  "name": "Sample statistics (Python)",
  "nodeType": "Code",
  "content": {
    "$type": "CodeConfiguration",
    "code": "import statistics\n…",
    "language": "python",
    "isExecutable": true
  }
}
```

Create your own through the Code editor (pick `python` in the language selector), over MCP, or the
SDK — then press **Run**. Any exception is captured and reported as a failed run; the worker never
wedges on a bad snippet.

## The flow

```
 Run a Code node (Language = "python")
        │  ExecuteScriptRequest
        ▼
 CodeNodeType.HandleExecuteScript ──creates──► ActivityLog node  (Status = Running)
        │  SubmitCodeRequest { Language = "python", ActivityLogPath }
        │  (C# would go in-process to the Roslyn kernel; python is re-targeted)
        ▼
   py/python-kernel  (a connected Python worker — clients/python)
        │  execute_python(code, Inputs): stdout + trailing-expression value
        ▼
   patch the ActivityLog node  (Status = Succeeded/Failed, messages, returnValue)
        │
        ▼
   every subscriber (the Code node's Output pane, the activity feed, tests) sees it
```

The only .NET-side change relative to C# is **where the submission is sent** —
`CodeNodeType.ResolveKernelAddress(language, activityPath)` picks the target: `python` →
`py/python-kernel`, `javascript`/`typescript` → `node/node-kernel` (the Node worker, `clients/typescript`),
everything else in-process. Each worker executes and patches the same ActivityLog, so js/ts output
surfaces exactly like python and C#. The concurrency-critical Roslyn `KernelExecutor` is untouched —
it only ever runs C#.

## Run a language worker

The worker lives in the Python SDK (`clients/python`). It connects as a **stable** participant
address so the kernel can target it:

```bash
cd clients/python
pip install -e .
bash scripts/gen_proto.sh                      # generate gRPC stubs from the canonical mesh.proto
python -m meshweaver.worker \
    --url https://memex.meshweaver.cloud \
    --token mw_…                                # validated server-side; the worker writes under this identity
    --address py/python-kernel                 # the address CodeNodeType routes python submissions to
```

It registers under `py/python-kernel`, then waits for `SubmitCodeRequest` deliveries. Each one runs
in a fresh namespace.

> **javascript / typescript** — the **Node worker** is the exact equivalent, from `clients/typescript`:
> `npm run build && node dist/worker.js --url … --address node/node-kernel`. It runs the snippet in a
> `vm` sandbox with the same REPL semantics (`console.log` captured, trailing expression = return value,
> `Inputs` global; TypeScript is transpiled first). Same wire contract, same Activity write-back.

> **Identity / access.** The worker writes the Activity node under the identity of its `--token`. That
> identity needs write access to where activities are stored (the runner's home partition). Use a token
> whose user can write there — the same access model as any participant.

## Ship it as a trusted sidecar (no token)

The command above is the *manual* / dev shape. In a deployment the worker ships **in the portal's own
pod** as a sidecar and connects to the portal's **trusted loopback gRPC endpoint** instead of an
external URL:

```bash
# built from deploy/python-gate/Dockerfile (the SDK package + the canonical proto)
python -m meshweaver.worker --url http://127.0.0.1:8082 --address py/python-kernel
```

There is **no `--token`**: the endpoint is bound to `127.0.0.1`, reachable only from containers in the
same pod, so reachability *is* the authentication (see `GrpcOptions.TrustedPort` and the trusted-gate
note in [A standalone hub in Python](../../DataMesh/PythonStandaloneHub)). This is the exact parity with
the in-process Roslyn kernel: the C# kernel runs in the portal process; the python gate runs in the
portal *pod*, and a run executes under the requesting user's identity (the gate echoes the delivery's
`AccessContext`), not a standing service credential — nothing to rotate.

Gates are **feature-flagged** per language under `grpc.gates` — every language is **included by
default**; a gate runs once its image is supplied (empty image ⇒ no sidecar). Set `enabled: false` to
opt a language out:

```yaml
grpc:
  gates:
    python:
      image: <registry>/meshweaver/python-gate:<tag>   # deploy/python-gate/Dockerfile
    node:
      image: <registry>/meshweaver/node-gate:<tag>     # deploy/node-gate/Dockerfile — runs js/ts
```

Each language is its own gate (`py/python-kernel`, `node/node-kernel`); the same shape adds the next
(`bun`, a language pool that leases a worker per run/partition). The routing branch
(`ResolveKernelAddress`) and the `grpc.gates` map are the two places that grow.

## Related

- @../../DataMesh/PythonPandasNode — the stateful counterpart: a Python **participant** holding a live `pandas.DataFrame`.
- @../../DataMesh/CallingPython — the stateless alternative: a C# cell shells out to `python3` through the bounded Process I/O pool.
- @../ForeignLanguageIntegration — the gRPC bridge and the SDK surface the worker is built on.
- @../../DataMesh/InteractiveMarkdown — executable fenced blocks in documentation pages (the fence language flows onto the submission).
