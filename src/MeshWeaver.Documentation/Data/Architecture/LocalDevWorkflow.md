---
NodeType: Markdown
Name: "Local Dev Workflow — restart, reload, debug"
Abstract: "Three ways to apply code changes to a running portal — hot reload via dotnet watch, dashboard restart, process kill. When to use which, what NOT to restart, and how to verify like CI on a Mac (host-built reference set, Testcontainers on Colima, leftover test containers)."
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Aspire"
  - "Localhost"
  - "DevWorkflow"
---

## Repository instructions and skills

`AGENTS.md` defines the repository rules. The maintained skill files live under
`.claude/skills/`; the tracked relative symlink `.agents/skills -> ../.claude/skills` exposes
the same files to Codex. Edit or add a skill in the canonical directory once. The directory
link also exposes newly added skills without a second manifest or synchronization step.
[Codex follows symlinked skill folders](https://learn.chatgpt.com/docs/build-skills).

A one-time import that copies skills into `.agents/skills/` creates an independent snapshot.
Those copies can become stale while the checked-in skills continue changing. If an existing
checkout has such an untracked directory, preserve it outside the checkout and review any
local edits before updating to the tracked link. Do not commit the copied tree or hide it with
an ignore rule. Git must materialize symlinks (`core.symlinks=true`, with filesystem support);
a plain text file containing the link target does not enable skill discovery.

`SkillFrontMatterGuard` verifies that Codex's discovery directory resolves to the canonical
skills and that the source skills parse. `NoConflictMarkersGuard` permits only this exact
relative alias; other tracked links, including absolute and dangling links, remain errors.
Machine-local `.codex/config.toml` MCP settings are separate from shared skill content.

## Quick reference

> 🚨 **The portal hosts live in `MeshWeaver.Plugins` since 2026-08-26** (MeshWeaver#2293 — the GUI
> extraction). `Memex.Portal.Monolith`, `Memex.AppHost` and `Memex.Portal.Distributed` are under
> `src/` **in that repository**, and so are the login pages (`Memex.Portal.Gui`). A core checkout on
> its own has no portal to run: following an older copy of this page from core alone gives a
> process with no login UI and no control views (#2367). Every command below assumes the standard
> sibling layout — `MeshWeaver/` and `MeshWeaver.Plugins/` side by side — which is what plugins'
> `src/Directory.Build.props` defaults `MeshWeaverRoot` to (`../MeshWeaver`); set
> `-p:MeshWeaverRoot=<path>` if yours differs.


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
  <text x="625" y="299" text-anchor="middle" fill="#ffb74d" font-size="12" font-weight="bold">③ Kill process</text>
  <text x="625" y="317" text-anchor="middle" fill="#ffa726" font-size="10">Kill &amp; Aspire auto-restarts · ~5 s</text>
</svg>

*All three options restart only `Memex.Portal.Distributed`; containers, dashboard, and OTLP collector stay up.*

| Approach | Typical cost | When to use |
|---|---|---|
| `dotnet watch --project ../MeshWeaver.Plugins/src/Memex.AppHost` | Seconds | Default. File save triggers a per-resource restart automatically. |
| Dashboard UI: Resources → ⋯ → **Restart** | ~10 s | Watch isn't running, or it missed a change. |
| Kill the portal process (`pkill -f Memex.Portal.Distributed`) | ~5 s | Last resort — dashboard hangs or AppHost is wedged. |

> **Do NOT** kill the whole `aspire` / `Memex.AppHost` process unless you changed AppHost wiring itself. A full restart costs 30–60 s, rebuilds every resource, re-launches the Postgres and blob-storage containers, and invalidates the dashboard's browser-token URL.

---

## Starting Aspire

> The image-based AppHost (`src/Memex.Deploy.AppHost` (MeshWeaver.Plugins), and any customer AppHost on the
> published `MeshWeaver.Aspire.Hosting.Memex` package) declares its instance as a **Deployment
> record** — `builder.AddMemex("memex").WithImage(…).WithPluginRepo(…)…` — the same record the
> Helm chart renders from and the portal binds at boot (`Deployment:Record`). `--record-out
> <path>` writes the final record for the setup wizard or a `Provision` action.
> [ConfiguringAnInstanceFromAspire](/Doc/Architecture/ConfiguringAnInstanceFromAspire).

Three modes — pick by whether you want to hold a terminal and whether you need a build:

```bash
# A. Interactive (foreground) — builds, holds the terminal, prints live status.
#    Registers with `aspire mcp` so MCP tools can drive it. Ctrl+C to stop.
aspire run --project ../MeshWeaver.Plugins/src/Memex.AppHost

# B. Background daemon — detaches and returns immediately; survives across shells.
#    `--no-build` reuses the existing binaries (fast — no rebuild). Drop --no-build
#    to build first. Also registers with `aspire mcp`.
aspire start --no-build --project ../MeshWeaver.Plugins/src/Memex.AppHost
aspire ps                 # list running AppHosts (Path · PID · dashboard URL)
aspire stop               # stop the background AppHost
aspire logs [<resource>]  # tail logs without the dashboard

# C. Visual Studio's "F5" path.
dotnet run --project ../MeshWeaver.Plugins/src/Memex.AppHost
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

Open that URL in your browser. The token is per-process — restart Aspire and you get a new one. To skip token authentication in development, add the following to `../MeshWeaver.Plugins/src/Memex.AppHost/Properties/launchSettings.json`:

```json
"environmentVariables": {
  "DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS": "true"
}
```

Visual Studio's F5 path does this implicitly via its launch profile, which is why it never prompts.

---

## Full stop → rebuild → restart (clean cold-start after a code change)

Hot reload (Option 1) and the per-resource restarts (Options 2–3) are the default. But when you
want a **clean cold-start with new code** — a non-trivial change spanning several referenced
projects, or recovery from a wedged stack — use the explicit stop → rebuild → start cycle. The two
steps people miss (and that cause the "I rebuilt but it still runs old code" / "my build won't
compile, file in use" loops) are in **bold**:

On macOS/Linux (zsh/bash):

```bash
# 1. Stop Aspire — AND force-kill, because `aspire stop` can exit while leaving the
#    AppHost + portal processes alive:
aspire stop
pkill -f Memex.AppHost; pkill -f Memex.Portal.Distributed

# 2. 🚨 RELEASE THE BUILD LOCKS. Lingering MSBuild / VB-C# compiler-server nodes keep
#    obj/*.dll open AFTER the AppHost is gone — that is the cause of the rebuild failing with
#    "CS2012: cannot open '…/obj/…/X.dll' for writing" or "MSB3021: being used by another
#    process". Killing the portal is NOT enough; you must shut the build server too:
dotnet build-server shutdown

# 3. Rebuild the portal project (it lives in the sibling MeshWeaver.Plugins checkout; MeshWeaverRoot
#    resolves back to this repo). Building Memex.Portal.Distributed also rebuilds every project it
#    references here (MeshWeaver.AI, .Graph, .Mesh.Contract, …) and in plugins (.Blazor.Portal), so ALL your edits
#    compile — this doubles as your compile gate before bringing the stack up:
dotnet build ../MeshWeaver.Plugins/src/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj --no-restore

# 4. Start fast, reusing the build from step 3 (no second compile):
aspire start --no-build --project ../MeshWeaver.Plugins/src/Memex.AppHost
```

On Windows (PowerShell), step 1's force-kill is instead:

```powershell
Get-Process Memex.AppHost,Memex.Portal.Distributed,aspire -ErrorAction SilentlyContinue | Stop-Process -Force
```

> **Why `--no-build` is safe here:** step 3 already produced current binaries for the portal and
> everything it references, so `--no-build` just launches them. The Postgres / blob containers are
> persistent and survive the stop, so the database is intact across the cycle.

---

## Option 1 — Hot reload with `dotnet watch`

```bash
dotnet watch --project ../MeshWeaver.Plugins/src/Memex.AppHost
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

```bash
pkill -f Memex.Portal.Distributed                                                   # macOS / Linux
```
```powershell
Get-Process Memex.Portal.Distributed -ErrorAction SilentlyContinue | Stop-Process -Force   # Windows
```

Aspire's resource watcher notices the exit and restarts the resource, typically within a few seconds. Use this when the dashboard UI is unresponsive (for example, a layout area is hung in JS and is blocking dashboard rendering), or when you want to confirm a clean cold-start.

---

## What NOT to restart

| Process | Only restart if… | Cost |
|---|---|---|
| `Memex.AppHost` | You changed AppHost wiring | 30–60 s + container relaunch |
| `aspire` (CLI) | You're switching projects | New dashboard token URL required |
| `dcp` × N | They crashed (`pgrep -l dcp`, or `Get-Process dcp` on Windows) | AppHost may not recover; full restart needed |
| Postgres / blob containers | You need a clean database | Full Aspire restart + migration replay |

---

## When `aspire mcp` doesn't see your AppHost

If `mcp__aspire__list_apphosts` returns `[]` even though Aspire is running, the AppHost was started via `dotnet run`. Only the **`aspire` CLI** writes the discovery file the MCP server reads — both `aspire run` and `aspire start` do; `dotnet run` does not.

Fix — stop the current AppHost and restart with either CLI form:

```bash
aspire run --project ../MeshWeaver.Plugins/src/Memex.AppHost     # foreground
aspire start --project ../MeshWeaver.Plugins/src/Memex.AppHost   # background daemon
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
| `Message contains "Could not deserialize"` | Type-registry mismatch — see [DebuggingMessageFlow.md](/Doc/Architecture/DebuggingMessageFlow) |

For distributed traces across resources, open `https://localhost:17200/traces` and click any trace to see span timing across the AppHost, Portal, and Postgres boundary.

---

## Verify like CI on a Mac

Three things used to make a Mac unable to verify the way CI does. Each one now has a supported route.

### In-mesh C#: a reference set built on this host

CI compiles and runs in-mesh C# (`<Type>/Source/*.cs`, `<Type>/Test/*.cs`) against the platform
**image**. That means the tester image's `/app`, and in MeshWeaver.Plugins the portal image's `/app` with
the tester's extra `MeshWeaver.*` assemblies laid over it. A Mac could not use either source:

| Reference set | `compile-check.py` | `run-node-tests.py` | Why |
|---|---|---|---|
| image extraction (`--image`, `fetch-refs.py`) | ✅ compiles | ❌ exit 3 | The assemblies are built for Linux. A process that loads them on macOS aborts, so the harness refuses rather than report clean content as `BUILD FAILED`. It also needs registry access. |
| core `src/*/bin` build | ❌ exit 4 | ❌ exit 2 | A library's `bin` holds only its own assembly, so there is no `System.Reactive.dll`. |
| **host-built** (`build-host-reference-set.py`) | ✅ | ✅ | The image's own closure, published on this host. |

`.github/scripts/build-host-reference-set.py` publishes that closure for this host's RID,
framework-dependent: the tester (`tools/MeshWeaver.PluginTester`), plus the portal
(`src/Memex.Portal.Distributed`) when you name a Plugins checkout. The portal is the base and the tester
adds only the `MeshWeaver.*` assemblies the portal lacks, the same rule `platform-mount.py` applies in CI.
The result is one flat directory, written next to a `meshweaver-reference-set.json` manifest.
`compile-check.py` classifies the directory as layout `host-build` **only** because of that manifest.
A flat directory without it is still an image extraction, and an image extraction missing its shared
frameworks is still refused. The manifest records the core commit. The set is refused (exit 3) once that
checkout's `origin/main` has moved past it, the same rule as a stale sibling checkout, so rebuild after
you update core.

```bash
# 1. Build the set from your CORE worktree, naming the Plugins checkout you test (≈1–3 min).
python3 <core>/.github/scripts/build-host-reference-set.py --plugins <plugins>
REFS=$(python3 <core>/.github/scripts/build-host-reference-set.py --plugins <plugins> --print-path)

# 2. The registry-served modules come from the Plugins checkout's own bin (never in an image).
#    Build them against the same core (compile-check names exactly these when they are missing):
dotnet build <plugins>/src/MeshWeaver.AI -c Release -p:MeshWeaverRoot=<core>/      # likewise .Markdown.Collaboration, .Maps

# 3. Run the launchers against THIS core's scripts and THIS set (from the Plugins checkout):
export MW_PLATFORM_SCRIPTS=<core>/.github/scripts
python3 scripts/canonical-compile-check.py --modules Hosting --refs "$REFS"
python3 devtools/run-node-tests.py Hosting --type InstanceAction --refs "$REFS"
```

Measured on darwin-arm64 (Apple silicon): the set held 224 assemblies; `canonical-compile-check.py
--modules Hosting` compiled 48 of 48 NodeTypes clean in 64 s; `run-node-tests.py Hosting --type
InstanceAction` ran 56 suites with 760 of 760 cases passing in 21 s. Old sets stay under
`~/.cache/meshweaver/refs/host-*` until you delete them. The default path is keyed by the SOURCE STATE:
the core commit, plus a digest of its uncommitted tracked changes when there are any, and the same for
the Plugins checkout. Many worktrees sit at one commit and share this cache, so two dirty worktrees never
build into the same path. Each build stages in a directory of its own, and a set already at its path is
kept, never replaced under a session that uses it as `--refs`. Only this host's RID is accepted: the
manifest declares the set host-built, which is what lets `run-node-tests.py` execute it here.

### Testcontainers on Colima: no hand-set variables

On Colima a published port reaches the Mac's `localhost` only while Colima's port forwarder is up.
When it is down, every Testcontainers fixture times out on `localhost:<port>`, even though the port is open
on the VM's own address. Ryuk also fails, because it mounts the Mac-side socket path, which does not exist
inside the VM. The hand workaround (a pgvector container started at the VM IP, `MW_TEST_PG_CONNECTION`,
`TESTCONTAINERS_RYUK_DISABLED=true`) is no longer needed, and it is why containers were left behind.

In MeshWeaver.Plugins, every project that references a `Testcontainers*` package compiles
`src/Testing.Shared/LocalDockerEndpoint.cs`, a module initializer. When the current Docker endpoint is a
Colima socket, it sets Testcontainers' documented Colima settings, each only while still unset:
`DOCKER_HOST`, `TESTCONTAINERS_HOST_OVERRIDE` (the VM address from `colima list --json`, which needs a VM
started with `--network-address`) and `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`. Ryuk
then runs, so a killed test host's containers are reaped. For any other Docker endpoint, and on CI, it does
nothing. A plain `dotnet test` works. Run it from the core checkout, whose `global.json` selects
Microsoft.Testing.Platform:

```bash
cd <core> && dotnet test --project <plugins>/src/MeshWeaver.Hosting.PostgreSql.Test/MeshWeaver.Hosting.PostgreSql.Test.csproj \
  -c Release --no-build --filter-class MeshWeaver.Hosting.PostgreSql.Test.BlankDescriptionFilterPgTests
```

🚨 Never run `memex-local up` to get a database for a test. It runs `az acr login` against the default
subscription.

### Leftover test containers

Every MeshWeaver fixture labels its container `org.meshweaver.test=<fixture>`, beside Testcontainers' own
`org.testcontainers*` labels, and disposes it on teardown. Ryuk reaps the containers of a host that died
first. For anything older, `scripts/clean-test-containers.py` in MeshWeaver.Plugins lists labelled test
containers older than `--older-than` hours (default 12), and removes them with `--apply`. It removes an
unlabelled container only when named with `--name`, and only for a Postgres image. It refuses `k8s_*`
containers and anything carrying a Kubernetes or Compose label (the memex-local stack), even when named. It
also refuses a running Postgres container that has a client connected.

```bash
python3 scripts/clean-test-containers.py                    # dry run
python3 scripts/clean-test-containers.py --older-than 6 --apply
```

---

## Common gotchas

**"No AppHost is currently running" from `aspire mcp`.**
The AppHost was started via `dotnet run`, not `aspire run`. Restart with the latter (see above).

**Builds fail with `MSB3021: cannot copy … being used by another process` or `CS2012: cannot open '…/obj/…/X.dll' for writing`.**
A previous AppHost/portal **and/or** a lingering MSBuild / VB-C# compiler-server node is holding the binaries open. Killing the portal alone is often not enough — the build-server nodes survive it. Force-kill the processes **and** run `dotnet build-server shutdown`, then retry. See [Full stop → rebuild → restart](#full-stop--rebuild--restart-clean-cold-start-after-a-code-change) for the full cycle.

**Portal's chat hangs at "Allocating agent…".**
Almost always one of two causes:

1. The portal hub's TypeRegistry doesn't have `AppendUserMessageResponse` registered — the response arrives as `RawJson` and the original `Observe` never resolves. Fix: `WithPortalConfiguration(c => { c.TypeRegistry.AddAITypes(); return c.AddData().WithGraphTypes(); })` in `MemexConfiguration` (already applied in `MemexConfiguration.ConfigureMemexPortal`).
2. Multiple portal hubs created per page navigation (the per-DI-scope shape) — a chat response routes to an already-disposed transient portal. Fix: the per-user portal address in `PortalApplication`.

Both issues are post-mortemed in commit messages on the routing branch — see `git log --grep "portal hub"`.
