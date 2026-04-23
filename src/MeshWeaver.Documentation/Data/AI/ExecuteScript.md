---
NodeType: "Doc/Article"
Title: "Executing Scripts via MCP"
Abstract: "Run an executable Code node's C# through the kernel from any MCP client — how to mark a node IsExecutable, call ExecuteScript, and observe Progress output."
Icon: "Play"
Published: "2026-04-23"
Thumbnail: "images/agenticai.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "MCP"
  - "Kernel"
  - "Scripts"
---

## Overview

MeshWeaver ships an MCP tool, **`ExecuteScript`**, that runs the C# code stored in an
executable **`Code`** MeshNode through the in-process
[Microsoft.DotNet.Interactive](https://github.com/dotnet/interactive) kernel and returns
a status envelope. Agents use this to run import scripts, test harnesses, or any
ad-hoc C# against the live mesh without needing a browser click.

Side effects — `mesh.CreateNode`, `mesh.UpdateNode`, blob writes — happen by the time
`ExecuteScript` returns.

## When to use it

| Use `ExecuteScript` | Don't use `ExecuteScript` |
|---|---|
| Running a data import (xlsx / CSV → MeshNodes) | Tool-level CRUD — use `Create` / `Update` / `Patch` / `Delete` instead |
| One-shot assertion harness ("test this calculation is green") | Anything conversational — let the agent reason |
| Triggering a scheduled job by hand | Rendering a view — use `RenderArea` |
| Reflection work that reads a NodeType's compiled assembly | Reading a single node — use `Get` |

Scripts are full C# — `#r "nuget:..."` directives work, the kernel's `Mesh` global
exposes the hub's service provider, and Rx operators compose cleanly.

## Making a Code node executable

Opt-in per node. Set `CodeConfiguration.IsExecutable = true`:

```json
{
  "id": "ImportLargeClaims",
  "namespace": "Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script",
  "name": "Import Large Claims",
  "nodeType": "Code",
  "content": {
    "code": "Console.WriteLine(\"hello\"); 1+1",
    "language": "csharp",
    "isExecutable": true
  }
}
```

Default is `false`, so existing Code nodes remain read-only unless you explicitly flip
the flag. The node's **Content** view in the portal surfaces a **Run** button next to
the Edit button when the flag is true.

## Calling ExecuteScript from MCP

```jsonc
// Tool call from your MCP client (Claude Code / Cursor / etc.)
{
  "name": "ExecuteScript",
  "arguments": {
    "path": "@Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script/ImportLargeClaims",
    "timeoutSeconds": 120
  }
}
```

The `path` resolves through the same Unified Content Reference rules as every other
MCP tool — leading `@` is stripped; `@/` prefixes go to absolute paths; relative
paths are resolved against the current chat context.

### Response shape

```jsonc
{
  "status": "Executed",          // or "Error" / "Timeout"
  "path": "Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script/ImportLargeClaims",
  "submissionId": "…",           // stable id for the run
  "kernelAddress": "kernel/code-Systemorph-FutuRe-EuropeRe-AcmeSubmission2025-Script-ImportLargeClaims",
  "outputUrl": "kernel/code-…/…",// layout area path holding this run's Console output
  "error": null,                 // populated on status=Error
  "message": "Code dispatched and kernel signalled completion…"
}
```

`status="Executed"` means the kernel posted a `SubmitCodeResponse` with `Success=true`
— the command ran without a diagnostic error from the C# compiler or runtime.
`status="Error"` carries the kernel's exception message in `error`. `status="Timeout"`
means the kernel didn't signal completion within `timeoutSeconds` — **side effects
may still have happened**; re-query the mesh to confirm.

### Watching Progress

Scripts can push live updates via the kernel's `Progress` global:

```csharp
Progress.Report("Fetched " + bytes.Length + " bytes. Parsing...");
```

Each call pushes into the kernel hub's `progress` layout area. Subscribers (Blazor
result pane, MCP `Get`) see the stream update live. To poll it from MCP:

```jsonc
{ "name": "Get", "arguments": { "path": "@kernel/code-…/area/progress" } }
```

The kernel address is stable per Code node (derived from the node path), so the same
`progress` URL works across runs — each new submission replaces the previous
progress string.

## Authoring scripts that agents can run

A few rules of thumb learned from the scripts that ship with the FutuRe demo:

1. **Use `Mesh.ServiceProvider` to reach hub-level services.** The kernel's script
   context exposes `Mesh` — call `Mesh.ServiceProvider.GetRequiredService<IMeshService>()`
   or `<IContentService>` as needed. The root hub is the one the kernel's sub-hub
   descends from, so DI surface is the full production set.

2. **Don't `await` hub-reachable services in hot paths.** Scripts run on the kernel's
   action block. `await meshService.CreateNodeAsync` inside a loop will serialise the
   hub. Prefer `meshService.CreateNode(node).Subscribe(...)` — it returns
   `IObservable<MeshNode>`, not `Task<MeshNode>`.

3. **Wrap external `Task`-returning primitives at the boundary with
   `Observable.FromAsync`.** For reading a blob:
   `Observable.FromAsync(() => contentService.GetContentAsync(...)).Subscribe(...)`
   keeps the kernel's action block free while the fetch runs on the task pool.

4. **Report liberally.** `Progress.Report` is best-effort and cheap — one hashtable
   write per call. The agent watching the run has no other visibility, so tell it
   what you're doing.

5. **End with a terminal marker.** `Progress.Report("DONE: created N nodes")` or
   `Progress.Report("FAIL: <reason>")` lets consumers (and you, scrolling through
   the progress area) see at a glance whether the run succeeded.

## Typical flow for an agent

```text
1. Search for the script:
   Search("nodeType:Code name:*import*", basePath="@Systemorph/FutuRe/EuropeRe")

2. Confirm it's executable:
   Get("@Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script/ImportLargeClaims")
   → check content.isExecutable == true

3. Run it:
   ExecuteScript(path=..., timeoutSeconds=120)
   → {status: "Executed", ...}

4. Verify side effects:
   Search("nodeType:Systemorph/FutuRe/LargeLoss",
          basePath="@Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Claims")
   → should now return the created claim nodes

5. (Optional) Fetch Progress stream for the human-readable trace:
   Get("@kernel/code-…/area/progress")
```

## Security

`ExecuteScript` runs C# with the full permissions of the authenticated caller. This
is intentional — scripts are mesh nodes and participate in the same RLS checks as any
other edit. An agent without Update rights on the target namespace can't create
children there, even from a script. But:

- **Anyone who can write a `Code` node with `IsExecutable=true` has full code
  execution on the server.** Treat that permission accordingly.
- **Don't paste secrets into a script.** Scripts are stored verbatim in the mesh and
  versioned. Pull credentials from a proper secret store or a scoped
  `IConfiguration` surface instead.

## Limitations

- `ExecuteScript` waits synchronously for kernel completion — long-running imports
  (many thousands of CreateNode calls) may hit the default 120 s timeout. Pass a
  higher `timeoutSeconds` for those.
- NuGet `#r` directives run against the same in-process resolver used by interactive
  markdown — new packages are fetched on first use (cached afterwards).
- The kernel itself is per-Code-node, not per-call. Two concurrent `ExecuteScript`
  calls on the same node contend for the same kernel; serialise them if that matters.

## Related

- [MCP Authentication](McpAuthentication) — how to mint tokens so an MCP client can
  call `ExecuteScript` at all.
- [Interactive markdown](../DataMesh/InteractiveMarkdown) — the markdown-driven
  equivalent used from `.md` files instead of button / tool calls.
- [Agentic AI](AgenticAI) — the broader agent-plugin story that `ExecuteScript`
  slots into.
