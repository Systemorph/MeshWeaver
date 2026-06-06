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

<svg viewBox="0 0 760 340" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="340" rx="10" fill="#1a1e2e" opacity="0.7"/>
  <text x="380" y="26" text-anchor="middle" fill="currentColor" fill-opacity="0.5" font-size="12">Aspire Stack — what each restart option touches</text>
  <rect x="20" y="38" width="720" height="56" rx="10" fill="#263238" stroke="#455a64" stroke-width="1.5"/>
  <text x="380" y="60" text-anchor="middle" fill="#b0bec5" font-size="11" font-weight="bold">Memex.AppHost  (aspire run)</text>
  <text x="380" y="80" text-anchor="middle" fill="#78909c" font-size="11">Orchestrator — restart only when AppHost wiring changes  ·  cost: 30–60 s</text>
  <rect x="40" y="110" width="210" height="54" rx="10" fill="#1b3a4b" stroke="#0288d1" stroke-width="1.5"/>
  <text x="145" y="133" text-anchor="middle" fill="#81d4fa" font-size="12" font-weight="bold">Postgres container</text>
  <text x="145" y="152" text-anchor="middle" fill="#4fc3f7" font-size="11">Survives all 3 options</text>
  <rect x="275" y="110" width="210" height="54" rx="10" fill="#1b3a4b" stroke="#0288d1" stroke-width="1.5"/>
  <text x="380" y="133" text-anchor="middle" fill="#81d4fa" font-size="12" font-weight="bold">Blob storage container</text>
  <text x="380" y="152" text-anchor="middle" fill="#4fc3f7" font-size="11">Survives all 3 options</text>
  <rect x="510" y="110" width="210" height="54" rx="10" fill="#1b3a4b" stroke="#0288d1" stroke-width="1.5"/>
  <text x="615" y="133" text-anchor="middle" fill="#81d4fa" font-size="12" font-weight="bold">Dashboard / OTLP</text>
  <text x="615" y="152" text-anchor="middle" fill="#4fc3f7" font-size="11">Survives all 3 options</text>
  <rect x="220" y="192" width="320" height="60" rx="10" fill="#1b2e1b" stroke="#66bb6a" stroke-width="2"/>
  <text x="380" y="217" text-anchor="middle" fill="#a5d6a7" font-size="13" font-weight="bold">Memex.Portal.Distributed</text>
  <text x="380" y="238" text-anchor="middle" fill="#81c784" font-size="11">Hub state · AI workers · SignalR sessions</text>
  <line x1="145" y1="164" x2="320" y2="192" stroke="#455a64" stroke-opacity="0.5" stroke-width="1" stroke-dasharray="4,3" marker-end="url(#arr)"/>
  <line x1="380" y1="164" x2="380" y2="192" stroke="#455a64" stroke-opacity="0.5" stroke-width="1" stroke-dasharray="4,3" marker-end="url(#arr)"/>
  <line x1="615" y1="164" x2="440" y2="192" stroke="#455a64" stroke-opacity="0.5" stroke-width="1" stroke-dasharray="4,3" marker-end="url(#arr)"/>
  <rect x="30" y="278" width="210" height="48" rx="8" fill="#1a2744" stroke="#1e88e5" stroke-width="1.5"/>
  <text x="135" y="299" text-anchor="middle" fill="#90caf9" font-size="12" font-weight="bold">① dotnet watch</text>
  <text x="135" y="317" text-anchor="middle" fill="#64b5f6" font-size="10">Auto file-save · seconds</text>
  <rect x="275" y="278" width="210" height="48" rx="8" fill="#1f2a1a" stroke="#66bb6a" stroke-width="1.5"/>
  <text x="380" y="299" text-anchor="middle" fill="#a5d6a7" font-size="12" font-weight="bold">② Dashboard restart</text>
  <text x="380" y="317" text-anchor="middle" fill="#81c784" font-size="10">Resources → ⋯ → Restart · ~10 s</text>
  <rect x="520" y="278" width="210" height="48" rx="8" fill="#2a1f1a" stroke="#f57c00" stroke-width="1.5"/>
  <text x="625" y="299" text-anchor="middle" fill="#ffb74d" font-size="12" font-weight="bold">③ Stop-Process</text>
  <text x="625" y="317" text-anchor="middle" fill="#ffa726" font-size="10">Kill &amp; Aspire auto-restarts · ~5 s</text>
</svg>

*All three options restart only `Memex.Portal.Distributed`; containers, dashboard, and OTLP collector stay up.*

| Approach | Typical cost | When to use |
|---|---|---|
| `dotnet watch --project memex/aspire/Memex.AppHost` | Seconds | Default. File save triggers a per-resource restart automatically. |
| Dashboard UI: Resources → ⋯ → **Restart** | ~10 s | Watch isn't running, or it missed a change. |
| `Stop-Process Memex.Portal.Distributed` | ~5 s | Last resort — dashboard hangs or AppHost is wedged. |

> **Do NOT** kill the whole `aspire` / `Memex.AppHost` process unless you changed AppHost wiring itself. A full restart costs 30–60 s, rebuilds every resource, re-launches the Postgres and blob-storage containers, and invalidates the dashboard's browser-token URL.

---

## Starting Aspire

Three modes — pick by whether you want to hold a terminal and whether you need a build:

```bash
# A. Interactive (foreground) — builds, holds the terminal, prints live status.
#    Registers with `aspire mcp` so MCP tools can drive it. Ctrl+C to stop.
aspire run --project memex/aspire/Memex.AppHost

# B. Background daemon — detaches and returns immediately; survives across shells.
#    `--no-build` reuses the existing binaries (fast — no rebuild). Drop --no-build
#    to build first. Also registers with `aspire mcp`.
aspire start --no-build --project memex/aspire/Memex.AppHost
aspire ps                 # list running AppHosts (Path · PID · dashboard URL)
aspire stop               # stop the background AppHost
aspire logs [<resource>]  # tail logs without the dashboard

# C. Visual Studio's "F5" path.
dotnet run --project memex/aspire/Memex.AppHost
```

> 🚨 **`aspire start --no-build` reuses the LAST build.** It's the fast way to bring the
> stack back up, but it does **not** pick up source changes — if you edited code, either
> drop `--no-build`, build the changed project first, or use one of the per-resource reloads
> below. Use `--no-build` when the binaries are already current (e.g. you just stopped Aspire
> to run a build and want it back up).

`aspire run` / `aspire start` print a one-shot dashboard token URL:

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
