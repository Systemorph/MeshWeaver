---
NodeType: Markdown
Name: "Local Dev Workflow — restart, reload, debug"
Abstract: "Three ways to apply code changes to a running portal — hot reload via dotnet watch, dashboard restart, process kill. When to use which, and what NOT to restart."
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Aspire"
  - "Localhost"
  - "DevWorkflow"
---

## Quick reference

When you change code in `Memex.Portal.Distributed` (or any project it references — `MeshWeaver.AI`, `MeshWeaver.Graph`, `MeshWeaver.Hosting.Orleans`, …), you have **three** ways to apply the change without touching the whole Aspire stack:

| Approach | Typical cost | When to use |
|---|---|---|
| `dotnet watch --project memex/aspire/Memex.AppHost` | Seconds | Default. File save triggers a per-resource restart automatically. |
| Dashboard UI: Resources → ⋯ → **Restart** | ~10 s | Watch isn't running, or it missed a change. |
| `Stop-Process Memex.Portal.Distributed` | ~5 s | Last resort — dashboard hangs or AppHost is wedged. |

> **Do NOT** kill the whole `aspire` / `Memex.AppHost` process unless you changed AppHost wiring itself. A full restart costs 30–60 s, rebuilds every resource, re-launches the Postgres and blob-storage containers, and invalidates the dashboard's browser-token URL.

---

## Starting Aspire

Two equivalent invocations:

```bash
# Visual Studio's "F5" path
dotnet run --project memex/aspire/Memex.AppHost

# CLI path — registers the AppHost with `aspire mcp` so MCP tools can drive it
aspire run --project memex/aspire/Memex.AppHost
```

`aspire run` prints a one-shot dashboard token URL in its first few lines of output:

```
Dashboard:  https://localhost:17200/login?t=<TOKEN>
```

Open that URL in your browser. The token is per-process — restart Aspire and you get a new one. To skip token authentication in development, add the following to `memex/aspire/Memex.AppHost/Properties/launchSettings.json`:

```json
"environmentVariables": {
  "DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS": "true"
}
```

Visual Studio's F5 path does this implicitly via its launch profile, which is why it never prompts.

---

## Option 1 — Hot reload with `dotnet watch`

```bash
dotnet watch --project memex/aspire/Memex.AppHost
```

Save a file and Aspire detects the change, rebuilds the affected project, and restarts only that resource. The Postgres container, blob-storage container, and dashboard all stay up. Most edits apply within seconds.

**What hot reload handles and what it doesn't:**

| Change type | Behaviour |
|---|---|
| Razor component edits | Applied in-place via Blazor hot reload — no restart. |
| Method body / addition changes | Applied in-place by the runtime hot-reload engine. |
| Type / interface / DI-registration changes | Triggers a resource restart (~5–10 s for the portal). |
| AppHost wiring changes | Requires a full Aspire restart — `dotnet watch` cannot reload its own host. |

---

## Option 2 — Dashboard restart

Navigate to `https://localhost:17200/` → **Resources** tab → find the resource (e.g. `memex-portal-distributed`) → click the **⋯** menu → **Restart**.

This is equivalent to running `dotnet build` and relaunching the resource process. The hub state is wiped (action blocks drained, hosted hubs disposed), but the dashboard, OTLP collector, and every other resource stay up.

**Use this when:**
- Watch isn't running.
- A hub-configuration change (`WithHandler<T>`, `WithRoutes`, `ConfigureDefaultNodeHub`) needs to take effect cleanly.
- Hot reload reports an unsupported edit.

---

## Option 3 — Process kill (last resort)

```powershell
Get-Process Memex.Portal.Distributed -ErrorAction SilentlyContinue | Stop-Process -Force
```

Aspire's resource watcher polls process state; when it detects the exit it restarts the resource within ~5 s. Use this when the dashboard UI is unresponsive (for example, a layout area is hung in JS and is blocking dashboard rendering), or when you want to confirm a clean cold-start.

---

## What NOT to restart

| Process | Only restart if… | Cost |
|---|---|---|
| `Memex.AppHost` | You changed AppHost wiring | 30–60 s + container relaunch |
| `aspire` (CLI) | You're switching projects | New dashboard token URL required |
| `dcp` × N | They crashed (`Get-Process dcp`) | AppHost may not recover; full restart needed |
| Postgres / blob containers | You need a clean database | Full Aspire restart + migration replay |

---

## When `aspire mcp` doesn't see your AppHost

If `mcp__aspire__list_apphosts` returns `[]` even though Aspire is running, the AppHost was started via `dotnet run` rather than `aspire run`. Only `aspire run` registers with the discovery file that the MCP server reads.

Fix — stop the current AppHost and restart with:

```bash
aspire run --project memex/aspire/Memex.AppHost
```

After this, MCP tools (`list_resources`, `list_traces`, `list_structured_logs`, `execute_resource_command`) all work against the running AppHost.

---

## Logging triage from the dashboard

Navigate to `https://localhost:17200/structuredlogs?level=info` and filter by:

| Filter | What you find |
|---|---|
| `category contains MessageHub` | Hub action-block events (`HUB_HANDLE_START/END`, `FinishDelivery`) |
| `category contains MessageService` | Routing failures (`"No handler found"`, `"Could not deserialize"`) |
| `category contains OrleansRoutingService` | Cross-grain dispatch warnings |
| `category contains Hosting.Orleans.MessageHubGrain` | Grain activation, `GrainDeliver IN/OUT` |
| `category contains GrainKeepAlive` | Heartbeat traffic — high volume suggests a stream leak |
| `Message contains SLOW_DISPATCH` | Per-message latency > 500 ms (instrumented in `MessageHub.HandleMessageAsync` and `OrleansRoutingService.DispatchObservable`) |
| `Message contains "Allocating agent"` | Chat starting — should be followed by `[ThreadExec]` lines |
| `Message contains "Could not deserialize"` | Type-registry mismatch — see [DebuggingMessageFlow.md](DebuggingMessageFlow.md) |

For distributed traces across resources, open `https://localhost:17200/traces` and click any trace to see span timing across the AppHost, Portal, and Postgres boundary.

---

## Common gotchas

**"No AppHost is currently running" from `aspire mcp`.**
The AppHost was started via `dotnet run`, not `aspire run`. Restart with the latter (see above).

**Builds fail with `MSB3021: cannot copy … being used by another process`.**
A previous AppHost or background process is holding the binaries open. Run the process-kill command above, then retry the build.

**Portal's chat hangs at "Allocating agent…".**
Almost always one of two causes:

1. The portal hub's TypeRegistry doesn't have `AppendUserMessageResponse` registered — the response arrives as `RawJson` and the original `Observe` never resolves. Fix: `WithPortalConfiguration(c => { c.TypeRegistry.AddAITypes(); return c.AddData().WithGraphTypes(); })` in `MemexConfiguration` (already applied in `MemexConfiguration.ConfigureMemexPortal`).
2. Multiple portal hubs created per page navigation (the per-DI-scope shape) — a chat response routes to an already-disposed transient portal. Fix: the per-user portal address in `PortalApplication`.

Both issues are post-mortemed in commit messages on the routing branch — see `git log --grep "portal hub"`.
