# Python Code Nodes — running Python through the .NET↔Python bridge

A **Code node** holds source and a `Language`. `csharp` (the default) runs **in-process** on the mesh's
Roslyn kernel. The in-process kernel only runs C# — there is no Python interpreter inside the .NET process,
and (by the actor-model rules) we do not want to block a hub thread on a subprocess. So a Code node whose
`Language` is `python` is **routed over the mesh to a connected Python worker** — the same gRPC bridge that
lets a Python process join the mesh as a first-class participant (see `ForeignLanguageBridge.md` and
`ForeignLanguageIntegration.md`). The worker executes the script and writes the result back onto the very
same **Activity node** every subscriber already watches, so a Python run surfaces output **identically** to
a C# one.

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

The only .NET-side change is **where the submission is sent**: C# stays in-process; `python` is addressed to
the worker. The concurrency-critical Roslyn `KernelExecutor` is untouched — it still only ever runs C#.

## 1. Run a Python worker

The worker lives in the Python SDK (`clients/python`). It connects as a **stable** participant address so
the kernel can target it:

```bash
cd clients/python
pip install -e .
bash scripts/gen_proto.sh                      # generate gRPC stubs from the canonical mesh.proto
python -m meshweaver.worker \
    --url https://atioz.meshweaver.cloud \
    --token mw_…                                # validated server-side; the worker writes under this identity
    --address py/python-kernel                 # the address CodeNodeType routes python submissions to
```

It registers under `py/python-kernel`, then waits for `SubmitCodeRequest` deliveries. Each one runs in a
fresh namespace: `print(...)` is captured as output, a **trailing bare expression** becomes the return value
(REPL semantics, mirroring Roslyn's `ScriptState.ReturnValue`), and `Inputs` exposes the caller's parameters.
Any exception is captured and reported as a failed run — the worker never wedges on a bad snippet.

> **Identity / access.** The worker writes the Activity node under the identity of its `--token`. That identity
> needs write access to where activities are stored (the runner's home partition). Use a token whose user can
> write there — the same access model as any participant.

## 2. Create a Code node of type `python`

A Python Code node is an ordinary Code node with `Language = "python"` and `IsExecutable = true`. Its content
is `CodeConfiguration { Code, Language = "python", IsExecutable = true }`. Create one through the editor, or
over MCP / the SDK, then press **Run** (or post `ExecuteScriptRequest`). Example script:

```python
import statistics

# `Inputs` carries caller-supplied parameters, exactly like the C# kernel's Inputs global.
samples = Inputs.get("samples", [3, 1, 4, 1, 5, 9, 2, 6])
print(f"n = {len(samples)}")

# A trailing expression is the node's return value (shown in the Output pane).
{"mean": statistics.mean(samples), "median": statistics.median(samples), "max": max(samples)}
```

Running it routes to `py/python-kernel`, which executes it and patches the run's Activity node with the
captured `print` output and the returned dict — visible in the Code node's **Output** pane and the activity
feed, just like a C# script.

## 3. In a documentation page

An executable fenced block carries its fence language onto the submission, so adding `--execute` to a
`python` block makes it a Python Code node embedded in the doc. The block below is **documentation-only** (no
`--execute`) on purpose — an auto-executing block would post to `py/python-kernel` on every page view, and
with no worker connected that routes nowhere. Add `--execute <id>` only on a page where a worker is
guaranteed (and the run is wanted):

```python
print("hello from a python worker on the mesh")
sum(range(10))
```

To make it live, change the fence to ` ```python --execute demo ` and start a worker (above).

## What is and isn't wired

- **Wired + verified.** The Python worker (`clients/python`: `execute_python` + `CodeWorker`, unit-tested),
  and the .NET routing — `SubmitCodeRequest` now carries `Language`, `CodeNodeType` re-targets `python` to
  the worker, and the markdown executable block forwards its fence language. All compile + the worker is tested.
- **Needs a running portal + worker to see end-to-end.** A live run against a portal with a connected worker,
  and pinning the `WIRE:`-marked shapes (the `ActivityLog` content fields and the `SubmitCodeResponse` shape
  the worker writes) against a captured sample. The execution core is correct; only those wire shapes await a
  live confirmation.
- **One worker, well-known address.** Today `python` targets a single `py/python-kernel`. A worker **pool**
  (lease a worker per run, or per partition) and other languages (`node`/`bun` → a `node/*` worker) are the
  natural next step — the routing branch is the one place that grows.
