---
NodeType: Markdown
Name: "Executing Scripts via MCP"
Abstract: "Run an executable Code node's C# through the kernel from any MCP client — how to mark a node IsExecutable, call ExecuteScript, and stream the run's ActivityLog for live progress."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#2e7d32'/><path d='M9 7l9 5-9 5z' fill='white'/></svg>"
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

The **`ExecuteScript`** MCP tool runs the C# code stored in an executable `Code` MeshNode through the in-process [Microsoft.DotNet.Interactive](https://github.com/dotnet/interactive) kernel and returns a status envelope. Agents use it to trigger data imports, run assertion harnesses, or execute any ad-hoc C# against the live mesh — all without a browser click.

> **Side effects are committed.** Calls to `mesh.CreateNode`, `mesh.UpdateNode`, and blob writes all happen before `ExecuteScript` returns.

## When to use it

Not every task calls for `ExecuteScript`. A quick decision table:

| Use `ExecuteScript` when… | Prefer a different tool when… |
|---|---|
| Running a data import (xlsx / CSV → MeshNodes) | Tool-level CRUD — use `Create` / `Update` / `Patch` / `Delete` |
| One-shot assertion harness ("check this calculation is green") | Anything conversational — let the agent reason directly |
| Triggering a scheduled job by hand | Rendering a view — use `RenderArea` |
| Reflection work that reads a NodeType's compiled assembly | Reading a single node — use `Get` |

Scripts are full C# — `#r "nuget:..."` directives work, the kernel's `Mesh` global exposes the hub's service provider, and Rx operators compose cleanly.

## Making a Code node executable

Execution is opt-in per node. Set `CodeConfiguration.IsExecutable = true` in the node's content:

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

The default is `false`, so existing Code nodes stay read-only until you explicitly flip the flag. When `isExecutable` is true, the portal's Content view surfaces a **Run** button alongside the Edit button.

## Calling ExecuteScript from MCP

Pass the node path and an optional timeout:

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

The `path` follows the same Unified Content Reference rules as every other MCP tool: the leading `@` is stripped, `@/` prefixes resolve to absolute paths, and relative paths are resolved against the current chat context.

### Response shape

```jsonc
{
  "status": "Executed",          // or "Error" / "Timeout"
  "path": "Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script/ImportLargeClaims",
  "submissionId": "…",           // stable id for this run
  "kernelAddress": "kernel/code-Systemorph-FutuRe-EuropeRe-AcmeSubmission2025-Script-ImportLargeClaims",
  "outputUrl": "kernel/code-…/…",// layout area path holding this run's Console output
  "error": null,                 // populated on status=Error
  "message": "Code dispatched and kernel signalled completion…"
}
```

| Status | Meaning |
|---|---|
| `Executed` | The kernel posted a `SubmitCodeResponse` with `Success=true` — no compiler or runtime error. |
| `Error` | The kernel reported a failure; the `error` field carries the exception message. |
| `Timeout` | The kernel did not signal completion within `timeoutSeconds`. **Side effects may still have occurred** — re-query the mesh to confirm. |

### Watching progress via the ActivityLog

Scripts emit live updates through the standard logger:

```csharp
Log.LogInformation("Fetched {Bytes} bytes. Parsing...", bytes.Length);
Log.LogWarning("Row {Row} skipped: {Reason}", i, reason);
Log.LogError("Import failed: {Message}", ex.Message);
```

Each call appends a message to the run's `ActivityLog` MeshNode. The `ExecuteScriptResponse` carries the log's path in the `activityLog` field. Clients subscribe to that path via `GetRemoteStream<MeshNode, MeshNodeReference>` and see each `ActivityLog.Messages` entry arrive in real time — the same shape used by Thread streams.

When the script finishes, `ActivityLog.Status` flips to `Succeeded` / `Warning` / `Failed` — the terminal signal UIs and agents watch for.

To poll the log from MCP:

```jsonc
{ "name": "Get", "arguments": { "path": "<activityLog-path-from-ExecuteScript-response>" } }
```

Each run gets its own `ActivityLog` node. Previous runs remain browsable under the Code node's activity history — no replacement, no bleed between submissions.

## Authoring scripts that agents can run

A few rules of thumb learned from the scripts that ship with the FutuRe demo:

**1. Reach hub-level services via `Mesh.ServiceProvider`.**
The kernel's script context exposes `Mesh`. Call `Mesh.ServiceProvider.GetRequiredService<IMeshService>()` or `<IContentService>` as needed. The root hub is the one the kernel's sub-hub descends from, so the full production DI surface is available.

**2. Avoid `await` on hub-reachable services in hot paths.**
Scripts run on the kernel's action block. `await meshService.CreateNodeAsync` inside a loop serialises the hub. Prefer `meshService.CreateNode(node).Subscribe(...)` — it returns `IObservable<MeshNode>`, not `Task<MeshNode>`.

**3. Wrap external `Task`-returning primitives at the boundary with `Observable.FromAsync`.**
For blob reads:
```csharp
Observable.FromAsync(() => contentService.GetContentAsync(...)).Subscribe(...)
```
This keeps the kernel's action block free while the fetch runs on the task pool.

**4. Log liberally.**
`Log.LogInformation(...)` / `LogWarning(...)` / `LogError(...)` append to the run's ActivityLog. Agents and users watching the log have no other window into what the script is doing — tell them.

**5. Let the ActivityLog status speak for you.**
On a clean run the log's `Status` ends at `Succeeded`; a `LogWarning` flips it to `Warning`; an exception or `LogError` flips to `Failed`. Consumers watch that field for the terminal signal — no need for synthetic DONE / FAIL markers.

## Typical agent flow

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

5. (Optional) Fetch the ActivityLog for the human-readable trace:
   Get(<activityLog path from the ExecuteScript response>)
```

## Security

`ExecuteScript` runs C# with the full permissions of the authenticated caller. Scripts are mesh nodes and participate in the same row-level security checks as any other edit — an agent without Update rights on the target namespace cannot create children there, even from a script.

> **Anyone who can write a `Code` node with `IsExecutable=true` has full server-side code execution.** Treat that permission accordingly.

> **Do not paste secrets into scripts.** Scripts are stored verbatim in the mesh and versioned. Pull credentials from a proper secret store or a scoped `IConfiguration` surface instead.

## Limitations

- `ExecuteScript` waits synchronously for kernel completion. Long-running imports (many thousands of `CreateNode` calls) may hit the default 120 s timeout — pass a higher `timeoutSeconds` for those.
- NuGet `#r` directives run against the same in-process resolver used by interactive markdown. New packages are fetched on first use and cached afterwards.
- The kernel is per-Code-node, not per-call. Two concurrent `ExecuteScript` calls on the same node contend for the same kernel — serialise them if that matters.

## Quick demo

The cell below shows what a minimal executable Code node's C# looks like when it runs in the kernel. This is the same execution environment your scripts run in:

```csharp --render ExecuteScriptDemo --show-code
var rows = new[]
{
    new { Step = 1, Action = "Search", Tool = "Search(\"nodeType:Code name:*import*\")" },
    new { Step = 2, Action = "Verify", Tool = "Get(\"@.../ImportLargeClaims\")" },
    new { Step = 3, Action = "Run",    Tool = "ExecuteScript(path=..., timeoutSeconds=120)" },
    new { Step = 4, Action = "Check",  Tool = "Search(\"nodeType:LargeLoss ...\")" },
};

MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Markdown("### Typical agent flow for `ExecuteScript`"))
    .WithView(MeshWeaver.Layout.Controls.Html(
        "<table style='width:100%;border-collapse:collapse'>" +
        "<tr><th style='text-align:left;padding:6px 8px;border-bottom:2px solid #ccc'>Step</th>" +
        "<th style='text-align:left;padding:6px 8px;border-bottom:2px solid #ccc'>Action</th>" +
        "<th style='text-align:left;padding:6px 8px;border-bottom:2px solid #ccc'>MCP Tool Call</th></tr>" +
        string.Concat(rows.Select(r =>
            $"<tr><td style='padding:6px 8px;border-bottom:1px solid #eee'>{r.Step}</td>" +
            $"<td style='padding:6px 8px;border-bottom:1px solid #eee'><b>{r.Action}</b></td>" +
            $"<td style='padding:6px 8px;border-bottom:1px solid #eee;font-family:monospace'>{r.Tool}</td></tr>")) +
        "</table>"))
```

## Related

- [MCP Authentication](McpAuthentication) — how to mint tokens so an MCP client can call `ExecuteScript` at all.
- [Interactive markdown](../DataMesh/InteractiveMarkdown) — the markdown-driven equivalent used from `.md` files instead of button or tool calls.
- [Agentic AI](AgenticAI) — the broader agent-plugin story that `ExecuteScript` slots into.
