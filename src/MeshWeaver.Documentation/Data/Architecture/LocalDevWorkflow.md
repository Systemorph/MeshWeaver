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

## TL;DR

When you change code in `Memex.Portal.Distributed` (or any project it references — `MeshWeaver.AI`, `MeshWeaver.Graph`, `MeshWeaver.Hosting.Orleans`, …), you have **three** ways to apply the change without nuking the whole Aspire stack:

| Approach | Cost | When to use |
|---|---|---|
| `dotnet watch --project memex/aspire/Memex.AppHost` | seconds | Default. File save triggers per-resource restart. |
| Dashboard UI: Resources → ⋯ → Restart | ~10 s | When watch isn't running, or the watch missed a change. |
| `Stop-Process Memex.Portal.Distributed` | ~5 s | Last resort — when the dashboard hangs or the AppHost is wedged. |

**Do NOT** kill the whole `aspire` / `Memex.AppHost` process unless you changed AppHost wiring itself. Full restart costs 30-60 s, rebuilds every resource, re-launches the Postgres + blob-storage containers, and loses the dashboard's browser-token URL.

## Starting Aspire

Two equivalent invocations:

```bash
# Visual Studio's "F5" path
dotnet run --project memex/aspire/Memex.AppHost

# CLI path — registers the AppHost with `aspire mcp` so MCP tools can drive it
aspire run --project memex/aspire/Memex.AppHost
```

`aspire run` prints a one-shot dashboard token URL on the first ~5 lines:

```
Dashboard:  https://localhost:17200/login?t=<TOKEN>
```

Open that URL in your browser. The token is per-process — restart Aspire and you get a new token. To skip token auth in dev, add to `memex/aspire/Memex.AppHost/Properties/launchSettings.json`:

```json
"environmentVariables": {
  "DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS": "true"
}
```

VS does this implicitly via its launch profile, which is why F5 doesn't prompt.

## Hot reload — `dotnet watch`

```bash
dotnet watch --project memex/aspire/Memex.AppHost
```

File save → Aspire detects the change, rebuilds the affected project, restarts only that resource. The Postgres container, blob-storage container, and dashboard stay up. Most edits apply within seconds.

**Limits:**
- Razor component changes apply via Blazor hot reload (no restart).
- C# changes that hot-reload supports (method bodies, additions) apply in-place.
- Type / interface / DI-registration changes trigger a resource restart (~5-10 s for the portal).
- AppHost wiring changes require a full Aspire restart — `dotnet watch` can't reload its own host.

## Dashboard restart

`https://localhost:17200/` → Resources tab → find the resource (e.g. `memex-portal-distributed`) → click the ⋯ menu → **Restart**.

Equivalent to: `dotnet build` + restart the resource process. The hub state is wiped (action blocks drained, hosted hubs disposed) but the dashboard, OTLP collector, and adjacent resources stay up.

Use when:
- Watch isn't running.
- A code change that requires hub reconfiguration (e.g. `WithHandler<T>`, `WithRoutes`, `ConfigureDefaultNodeHub`) needs to take effect cleanly.
- Hot reload reports an unsupported edit.

## Process kill — last resort

```powershell
Get-Process Memex.Portal.Distributed -ErrorAction SilentlyContinue | Stop-Process -Force
```

Aspire's resource watcher polls process state; when it sees the exit it restarts the resource within ~5 s. Use when the dashboard UI is unresponsive (e.g., a layout area is hung in JS and blocks dashboard rendering), or when you want to confirm a clean cold-start.

## What NOT to restart

| Process | Don't kill unless | Cost of full restart |
|---|---|---|
| `Memex.AppHost` | You changed AppHost wiring | 30-60 s + container relaunch |
| `aspire` (CLI) | You're switching projects | New dashboard token URL |
| `dcp` × N | They crashed (check `Get-Process dcp`) | AppHost may not recover; full restart needed |
| Postgres / blob containers | You need a clean DB | Full Aspire restart + migration replay |

## When `aspire mcp` doesn't see your AppHost

If `mcp__aspire__list_apphosts` returns `[]` even though Aspire is running, the AppHost wasn't started via `aspire run` (which registers with the discovery file the MCP reads). `dotnet run` on the AppHost project doesn't register.

Fix: stop the current AppHost and restart via:

```bash
aspire run --project memex/aspire/Memex.AppHost
```

After this, MCP tools (`list_resources`, `list_traces`, `list_structured_logs`, `execute_resource_command`) work against the running AppHost.

## Logging triage from the dashboard

`https://localhost:17200/structuredlogs?level=info` — filter by:

| Filter | What you find |
|---|---|
| `category contains MessageHub` | Hub action-block events (HUB_HANDLE_START/END, FinishDelivery) |
| `category contains MessageService` | Routing failures ("No handler found", "Could not deserialize") |
| `category contains OrleansRoutingService` | Cross-grain dispatch warnings |
| `category contains Hosting.Orleans.MessageHubGrain` | Grain activation, GrainDeliver IN/OUT |
| `category contains GrainKeepAlive` | Heartbeat traffic — too many = stream leak suspect |
| `Message contains SLOW_DISPATCH` | Per-message latency > 500 ms (instrumentation in `MessageHub.HandleMessageAsync` and `OrleansRoutingService.DispatchObservable`) |
| `Message contains "Allocating agent"` | Chat starting — should be followed by `[ThreadExec]` lines |
| `Message contains "Could not deserialize"` | Type-registry mismatch — see [DebuggingMessageFlow.md](DebuggingMessageFlow.md) |

`https://localhost:17200/traces` — distributed traces across resources. Click a trace to see span timing across the AppHost / Portal / Postgres boundary.

## Common gotchas

**"No AppHost is currently running" from `aspire mcp`.** AppHost was started via `dotnet run`, not `aspire run`. Restart with the latter (see above).

**Builds fail with `MSB3021: cannot copy ... being used by another process`.** A previous AppHost or background process is holding the binaries open. Run the kill command, then retry build.

**Portal's chat hangs at "Allocating agent…".** Almost always one of:
1. Portal hub's TypeRegistry doesn't have `AppendUserMessageResponse` registered → response arrives as RawJson and the original Observe never resolves. Fix is `WithPortalConfiguration(c => { c.TypeRegistry.AddAITypes(); return c.AddData().WithGraphTypes(); })` in `MemexConfiguration` (already applied in `MemexConfiguration.ConfigureMemexPortal`).
2. Multiple portal hubs created per page navigation (the per-DI-scope shape) — chat response routes to a disposed transient portal. Fix is the per-user portal address in `PortalApplication`.

Both issues are post-mortemed in commit messages on the routing branch — see `git log --grep "portal hub"`.
