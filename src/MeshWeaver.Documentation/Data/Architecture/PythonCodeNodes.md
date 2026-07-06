---
Name: Python Code Nodes
Category: Architecture
Description: Code nodes with Language = python route over the mesh to a connected Python worker — the actual node, the worker command, and the execution flow.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2c-4 0-5 2-5 4v3h5v1H5c-2 0-3 1.5-3 4s1 4 3 4h2v-3c0-2 1-3 3-3h5c1.7 0 3-1.3 3-3V6c0-2-1-4-6-4z"/><path d="M12 22c4 0 5-2 5-4v-3h-5v-1h7c2 0 3-1.5 3-4"/></svg>
---

# Python Code Nodes

A **Code node** holds source and a `Language`. `csharp` (the default) runs **in-process** on the mesh's
Roslyn kernel; a Code node whose `Language` is `python` is **routed over the mesh to a connected Python
worker** — the same gRPC bridge that lets a Python process join the mesh as a first-class participant
(see [Foreign Language Integration](../ForeignLanguageIntegration)). The worker executes the script and writes
the result back onto the very same **Activity node** every subscriber already watches, so a Python run
surfaces output **identically** to a C# one.

## The node

This is a real Code node — `SampleStatistics` ("Sample statistics (Python)"), shipped in the Doc
partition alongside this page (so it resolves wherever the docs are served) and embedded live. Press
**Run** in its toolbar: the run routes to the `py/python-kernel` worker and the output attaches
directly below the code (with no worker connected, the run's activity reports the failure in the same
output pane — nothing hangs):

@@Doc/Architecture/PythonCodeNodes/SampleStatistics

The node is nothing more than `CodeConfiguration { Code, Language = "python", IsExecutable = true }`:

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

Create your own through the Code editor (pick `python` in the language selector), over MCP, or the SDK —
then press **Run** (or post `ExecuteScriptRequest`). Inside the script, `print(...)` is captured as output,
a **trailing bare expression** becomes the return value (REPL semantics, mirroring Roslyn's
`ScriptState.ReturnValue`), and `Inputs` exposes the caller's parameters. Any exception is captured and
reported as a failed run — the worker never wedges on a bad snippet.

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

The only .NET-side change relative to C# is **where the submission is sent**: C# stays in-process; `python`
is addressed to the worker. The concurrency-critical Roslyn `KernelExecutor` is untouched — it only ever
runs C#.

## Run a Python worker

The worker lives in the Python SDK (`clients/python`). It connects as a **stable** participant address so
the kernel can target it:

```bash
cd clients/python
pip install -e .
bash scripts/gen_proto.sh                      # generate gRPC stubs from the canonical mesh.proto
python -m meshweaver.worker \
    --url https://memex.meshweaver.cloud \
    --token mw_…                                # validated server-side; the worker writes under this identity
    --address py/python-kernel                 # the address CodeNodeType routes python submissions to
```

It registers under `py/python-kernel`, then waits for `SubmitCodeRequest` deliveries. Each one runs in a
fresh namespace.

> **Identity / access.** The worker writes the Activity node under the identity of its `--token`. That identity
> needs write access to where activities are stored (the runner's home partition). Use a token whose user can
> write there — the same access model as any participant.

## Ship it as a trusted sidecar (no token)

The command above is the *manual* / dev shape. In a deployment the worker ships **in the portal's own pod**
as a sidecar and connects to the portal's **trusted loopback gRPC endpoint** instead of an external URL:

```bash
# built from deploy/python-gate/Dockerfile (the SDK package + the canonical proto)
python -m meshweaver.worker --url http://127.0.0.1:8082 --address py/python-kernel
```

There is **no `--token`**: the endpoint is bound to `127.0.0.1`, reachable only from containers in the same
pod, so reachability *is* the authentication (see `GrpcOptions.TrustedPort` and the trusted-gate note in
[A standalone hub in Python](/Doc/DataMesh/PythonStandaloneHub)). This is the exact parity with the in-process
Roslyn kernel: the C# kernel runs in the portal process; the python gate runs in the portal *pod*, and a run
executes under the requesting user's identity (the gate echoes the delivery's `AccessContext`), not a
standing service credential — nothing to rotate.

Enable it in the Helm chart (OFF by default; the deployment must supply the image):

```yaml
grpc:
  pythonGate:
    enabled: true
    image: <registry>/meshweaver/python-gate:<tag>   # deploy/python-gate/Dockerfile
```

Today `python` targets the single well-known `py/python-kernel` address. A worker **pool** (lease a worker
per run, or per partition) and other languages (`node`/`bun` → a `node/*` gate) are the natural next step —
the routing branch and the sidecar list are the two places that grow.

## Related

- [A pandas node in Python](/Doc/DataMesh/PythonPandasNode) — the stateful counterpart: a Python **participant** holding a live `pandas.DataFrame`.
- [Calling Python from MeshWeaver](/Doc/DataMesh/CallingPython) — the stateless alternative: a C# cell shells out to `python3` through the bounded Process I/O pool.
- [Foreign Language Integration](../ForeignLanguageIntegration) — the gRPC bridge and the SDK surface the worker is built on.
- [Interactive Markdown](/Doc/DataMesh/InteractiveMarkdown) — executable fenced blocks in documentation pages (the fence language flows onto the submission).
