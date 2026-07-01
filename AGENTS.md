# AGENTS.md

This file provides guidance to AI agents working with this repository.

## Git Workflow

**NEVER commit or push automatically.** Always wait for the user to explicitly ask.

### 🚨 Before you push: make CI green LOCALLY first — don't discover red on CI

CI builds **Release with warnings-as-errors**: `dotnet build --no-restore -c Release -p:CIRun=true -warnaserror`. A plain local `dotnet build` (Debug, no `-warnaserror`) passes while CI fails — warnings are promoted to errors there. Pushing a red branch wastes a CI cycle and, per the green-merge gate, blocks the pull-based self-update if it reaches main. So **before every push**:

1. **Sync with `main` first.** `git fetch origin main && git merge origin/main` (or rebase). A PR check builds your branch **merged with current main** — a stale branch inherits main's state (including any half-committed-WIP red, e.g. a `.razor` referencing a type whose `.cs` wasn't committed), and you discover it only on CI. Build what CI builds.
2. **Build with CI's flags**: `dotnet build -c Release -warnaserror` for at least the projects you touched and their dependents. Green here ⇒ green there for compile/warning errors. The classic miss: **CS9107** — a primary-constructor parameter captured *and* passed to a base ctor (warning in Debug, ERROR under `-warnaserror`). Fix it at the root: use the base's exposed member (e.g. `protected Output`) instead of capturing the param; do NOT just `NoWarn` it.
3. **Only push when that Release/`-warnaserror` build is clean.** Then verify the PR check went green (`gh pr checks`) before declaring done.

Full PR/merge gate: the `pullrequest` skill (CI must be GREEN before merge — main's image feeds the self-update).

## 🚨🚨🚨 ABSOLUTE: No band-aids — root cause only, literally always

**The user is LITERALLY NEVER interested in a band-aid, workaround, mitigation, or symptom-suppression.** When something hangs, deadlocks, flakes, or errors, find the EXACT defect and fix THAT — never paper over it.

These are band-aids, and proposing one as "the fix" is forbidden:
- **Increasing a bound to make it pass**: pool size, timeout, retry count, buffer size, `maxParallelThreads`. The question is never "how do I get more headroom" — it's "why is the slot/thread/budget not being released, or why is it erroring." A capped pool that exhausts means a slot is leaked or blocked; a timeout that trips means something never completes — fix the leak/block/non-completion.
- **A watchdog / timer / poller that resubscribes or retries** to recover from a state that "shouldn't happen." If the initial state never arrives, find why it's dropped/erroring — don't add a timer to paper over it. (The 2026-06-08 prod outage was exactly this: an initial-state retry watchdog amplified a mishandled error into a resubscribe storm.)
- **`catch {}` / swallow-and-continue / `.Catch(Observable.Empty)`** that hides a fault instead of surfacing or fixing it.
- **Revert-and-move-on** when the revert just hides a defect that's still live underneath.
- **A `Clear()` for test isolation, a widened `.Timeout(...)`, a sleep** — each is the *tell* of an unfixed root cause.

If active bleeding genuinely needs a stopgap before the real fix lands, say so EXPLICITLY: "this is a temporary stopgap; the root cause is X; I will fix X" — then fix X. Default to writing a **deterministic repro** (a concurrency/deadlock test if that's the failure mode) that pins the true cause before changing code, so the fix is proven, not guessed. Full reference: memory `feedback_no_bandaids`.

## 🚨🚨🚨 ABSOLUTE: No hand-woven async/concurrency primitives — the actor model does NOT tolerate `SemaphoreSlim`

**A `SemaphoreSlim` (or any hand-rolled async gate / lock-for-async / signal) anywhere in `src/` is FORBIDDEN — outside the one place sealed inside `IoPool`.** `SemaphoreSlim.WaitAsync()` blocks/parks a thread and its continuation captures the awaiting scheduler. On a hub it parks the single-threaded action block (or a grain turn) → the message you're waiting on can never be processed → **deadlock**. This is the same defect class as `async`/`await`/`Task<T>` in hub code; a `SemaphoreSlim` is just a lock-shaped version of it.

- **Serialization channels through the hub, never a semaphore.** "Only one at a time" / "wait your turn" is what the hub's single-threaded action block already gives you for free. When you need ordered, one-at-a-time processing, push items into a `Subject<T>` and run them with `.Select(Run).Concat().Subscribe(...)` (Concat subscribes the next only after the previous completes — order without a lock; the canonical fix is `KernelExecutor`'s REPL queue, which **replaced** a hand-woven `SemaphoreSlim`), or route state changes through `GetMeshNodeStream(path).Update(...)` (the owning hub serialises every writer).
- **Concurrency bounding / one-shot init / "run once" channels through `IIoPool`.** A bounded I/O gate, a promise-cached one-shot (schema provisioning, blob-cache init, connect handshake), a "first caller does it, the rest wait" — that is `pool.Run(...)` cached in an **instance** `ConcurrentDictionary` (the promise-cache; ReplaySubject-backed: runs once, replays to all). NOT a `SemaphoreSlim(1,1)` `_initLock` / `_connectGate`.
- **`Task`-as-a-gate is the same sin.** `TaskCompletionSource` used to make callers "await a signal", a `Task.Delay` timeout race, `ManualResetEventSlim`, `lock`-around-`await` — all hand-woven async. Make the **source observable** (`AsyncSubject`/`Subject` + `Concat`) and `Subscribe`, or push it onto `IIoPool`.

**The ONLY sanctioned `SemaphoreSlim` is the one sealed inside `IoPool` itself** (`MeshWeaver.Mesh.Threading`) — it IS the single boundary between the turn-based hub schedulers and genuinely-async I/O leaves, running work OFF the hub with `ConfigureAwait(false)`. Everywhere else, a `SemaphoreSlim` is a bug to delete. The litmus test: if your gate runs on (or is awaited from) a hub action block / grain turn / Blazor circuit, it deadlocks — channel it through a hub or `IIoPool` instead. Full reference: [ControlledIoPooling.md](src/MeshWeaver.Documentation/Data/Architecture/ControlledIoPooling.md), [AsynchronousCalls.md](src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md), memory `feedback_no_semaphoreslim`.

## 🚨🚨🚨 ABSOLUTE: Never hand-roll UI / data-binding / persistence / submit — use the framework

**A "UI feature" means wiring up the framework's EXISTING pieces, never reinventing them.** The framework already does data binding, node-content editing, auto-persistence, picking, **rendering (tables/lists via `DataGrid` and the typed controls)**, and thread submission ONE standard way that every layout area uses. Hand-rolling a parallel version — including emitting raw HTML strings instead of using a control — is FORBIDDEN.

- **Editing a mesh node's content, data-bound + auto-persisting** → bind the GUI client DIRECTLY to the node stream: declare `MeshNodeContentEditorControl.ForType(path, typeof(MyContent))` (simple scalar/bool fields) and the Blazor view reads from `Hub.GetMeshNodeStream(path)` and writes per-field via `GetMeshNodeStream(path).Update(...)`. ONE source of truth — the node stream. Rich content (markdown/picking) → already-node-bound controls (`MarkdownEditorControl.WithAutoSave`, `MeshNodePickerControl`, `CollaborativeMarkdownView`).
- **🚨 NEVER replicate the node into a layout-area `/data/{id}` copy + a server-side save subscription.** `host.UpdateData(id, node.Content)` + `GetDataStream(id).Debounce/Throttle.Subscribe(...GetMeshNodeStream(path).Update...)` — a.k.a. `OverviewLayoutArea.SetupAutoSave` / any `*AutoSave` helper / a "Save" button that reads `/data` and writes the node — is the FORBIDDEN replicate-then-save antipattern (two stores drift; the save loop clobbers unedited fields). The "standard EditNode / MeshNodePropertyEditor" editor IS this antipattern — do not copy it; migrate it.
- **Picking a mesh node** → `[MeshNode("query")]` → `MeshNodePickerControl` (stores the node PATH). **Form controls** → the `Edit` macro + `[UiControl<T>]`/`[Description]`/`[Editable(false)]`. Don't hand-build selects/checkboxes/textareas + a data section.
- **🚨 Rendering tabular / structured data → a framework CONTROL, NEVER hand-built HTML.** Tables → `Controls.DataGrid(rows).WithColumn(new PropertyColumnControl<T> { Property = nameof(Row.X).ToCamelCase() }.WithTitle("…").WithFormat("N0"))` bound to a plain row record (sorting / formatting / theming / virtualization for free; column API: `samples/Graph/Data/Cornerstone/Pricing/Source/PricingLayoutAreas.cs`). Compose `Controls.Stack` / `Controls.LayoutGrid` / `Controls.Title` / `Controls.Markdown`. **FORBIDDEN: building HTML strings** — `StringBuilder`/`$"<table>…"`/`$"<td>…"`, any `RenderHtml`-shaped helper, or `Controls.Html(handBuiltMarkup)` for structured data (`Controls.Html` is ONLY for genuinely pre-rendered markdown/rich text). This is the exact hack the user banned 2026-06-19 (*"use just controls and layout areas … get rid of RenderHtml … I don't want to see such hacks any more"*) — and the hand-rolled `RenderHtml`'s string-interpolation + LINQ also caused the >10-min MeshWeaver.AI compile regression (e30e9b5f1). If you're reaching for a string of HTML, STOP and find the control.
- **Submitting a chat message** → existing `hub.StartThread(...)` / `hub.SubmitMessage(...)` (see the thread tests: `client.SubmitMessage(threadPath, text, …)`). No wrapper class, no path→id resolution, no create-or-submit logic beyond those APIs. Pass node PATHS through; downstream loads the node (e.g. `StartThread` takes a model PATH and loads the model from its mesh-node stream — don't pre-resolve an id).
- **Never** `.Take(1)` on a stream feeding a live data-bound view — it freezes the binding (GUI/DataBinding.md).

Before writing ANY UI/binding/persistence code, FIND the existing framework area/control/macro/extension and use it. If you're reaching for `GetDataStream`/`Subscribe`/`Update`/`CombineLatest`/a new wrapper for a UI feature, STOP. Full reference: memory `feedback_no_handrolling`; GUI/DataBinding.md; the data-bind tests (`InlineEditingWorkflowTest`).

## 🚨🚨🚨 ABSOLUTE: Never change log levels in code for debug reasons

**Editing `LogInformation` ↔ `LogDebug` ↔ `LogTrace` (or `appsettings.json` under `src/`) to dial verbosity up or down for a debugging session is FORBIDDEN.** Log levels in code reflect the production cost model — `Information` lines ship to Loki (pod stdout → Promtail) and ingest/storage isn't free. Changing them temporarily silently bleeds budget the next CI run.

To turn the volume up for debugging, **edit the appsettings.json in the test's `bin/Debug/net10.0/` (or the equivalent runtime config)** — `reloadOnChange: true` is wired so the level flips mid-run without a rebuild. The src-tree `appsettings.json` and every `Log*` call in `src/` is committed contract.

If a Log call is genuinely too noisy or too quiet at the level it's written, fix it permanently with a real commit message explaining the cost/value trade-off — don't sneak it in alongside an unrelated change.

## Test Triage

When CI fails, **DO NOT run entire test projects** — iterate one test at a time:

1. Read failed test names from CI logs (`gh run view <id> --log`)
2. `dotnet test <project> --filter "FullyQualifiedName~<TestName>" --no-build --no-restore`
3. **No skipping** — CI-only failures catch real timing/state bugs

Full guidance: [WritingTests.md](src/MeshWeaver.Documentation/Data/Architecture/WritingTests.md) · [CqrsAndContentAccess.md](src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md) · [TestStateIsolation.md](src/MeshWeaver.Documentation/Data/Architecture/TestStateIsolation.md)

## GitHub PR Operations

`gh` CLI has **read + push** only — cannot merge, resolve threads, or request reviewers.

```bash
# Find unresolved review threads
gh api graphql -f query='query($owner:String!, $repo:String!, $pr:Int!) { repository(owner:$owner, name:$repo) { pullRequest(number:$pr) { reviewThreads(first:100) { nodes { id isResolved } } } } }' \
  -f owner=Systemorph -f repo=MeshWeaver -F pr=PR_NUMBER \
  --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved==false) | .id'
# Resolve a thread
gh api graphql -f query='mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ clientMutationId }}' -f id=THREAD_ID
gh pr merge PR_NUMBER --merge
```

**If `FORBIDDEN`**: re-authenticate with `! gh auth login`.

## 🚨 Postgres: One Schema Per Partition

**`public.mesh_nodes` is empty by design.** Data lives in per-partition schemas (`acme.mesh_nodes`, `rbuergi.mesh_nodes`, etc.).

Satellite table routing by path segment:

| Path segment | Table |
|---|---|
| `…/_Access/…` | `access` |
| `…/_Thread/…` | `threads` |
| `…/_Activity/…` | `activities` |
| `…/_Comment/…`, `_Approval`, `_Tracking` | `annotations` |
| `…/Source/…` or `…/Test/…` | `code` |
| (none) | `mesh_nodes` |

**`namespace` keeps the partition prefix — never strip it.** `namespace = rbuergi/ApiToken`, not `ApiToken`.

**Never run raw `psql UPDATE` on a live portal** — bypasses the workspace cache. Use `MoveNodeRequest` or add a Repair vN migration. If you must SQL-edit, restart `Memex.Portal.Distributed`.

**🚨 Partition schema: provision + existence are REACTIVE + POOLED — never declare a `PartitionDefinition` node to force a schema, never lowercase by hand.** The standard surface is on `IPartitionStorageProvider`:
- `EnsurePartitionProvisioned(namespace) : IObservable<Unit>` — the ONE entry point that creates a partition's schema + tables. Reactive, idempotent (promise-cached), and **pooled** on the `pg:{adapter}` IoPool (the PG impl lowercases the schema correctly). Subscribe it; compose with `.SelectMany(_ => write…)` before writing to a not-yet-provisioned partition.
- `PartitionExists(namespace) : IObservable<bool?>` — reactive existence check (`null` = indeterminate; OR-fold across providers as `PartitionWriteGuardValidator` does).

The router maps a path's first segment to `seg.ToLowerInvariant()`; a `PartitionDefinition` with `Schema` left null provisions the schema **verbatim** (`"Agent"` capital) while writes hit `"agent"` → 42P01. So the way to make code that writes a not-yet-provisioned partition work is `EnsurePartitionProvisioned(p).SelectMany(_ => write…)` — **not** a partition-def node. The async schema DDL runs inside the IoPool, never `Observable.FromAsync` (see [ControlledIoPooling.md](src/MeshWeaver.Documentation/Data/Architecture/ControlledIoPooling.md)).

Full reference: [PostgresSchemaArchitecture.md](src/MeshWeaver.Documentation/Data/Architecture/PostgresSchemaArchitecture.md)

## 🛡️ Global admin = admin on the Admin partition

**"Global/platform admin" has ONE meaning: `Permission.All` at scope `Admin`** — an `AccessAssignment` granting the `Admin` role in the **`Admin/_Access`** namespace (`Admin` is a standard partition, schema `admin`, that holds platform-level data). This is a **platform admin, NOT a data superuser**: `Admin/_Access` is Admin-scoped — it does NOT grant access to spaces or user partitions. Standing access = platform management (invites, deletes, config); emergency cross-partition data change requires explicit **elevation (break-glass)**, never standing. A **root** `_Access` grant is the data-superuser shape and is deliberately NOT how platform admins are provisioned.

- **The one predicate is `hub.IsGlobalAdmin()` / `hub.IsGlobalAdmin(userId)`** (`HubPermissionExtensions`). Every gate goes through it — NEVER an ad-hoc role-name (`Roles.Contains("PlatformAdmin")`) or root-scope (`GetEffectivePermissions("")`) check.
- **The grant lives in `Admin/_Access`, never root `_Access`.** Writers (`GlobalAdminSeed` from `Auth:GlobalAdmins`, `UserOnboardingService.GrantPlatformAdmin`) and readers (`AdminMenuGate`, `UserNodeType.GetGlobalAdminTabAsync`, `UserProfile`) must agree on the Admin partition — a writer/reader split (root vs Admin) silently locks admins out of every admin tab (2026-06-08).

Full reference: [AccessControl.md](src/MeshWeaver.Documentation/Data/Architecture/AccessControl.md) → "The Admin partition".

## Documentation

All docs embedded in `src/MeshWeaver.Documentation/` and served under `Doc/` at runtime.

| Topic area | Path |
|---|---|
| Architecture | `src/MeshWeaver.Documentation/Data/Architecture/` |
| DataMesh | `src/MeshWeaver.Documentation/Data/DataMesh/` |
| GUI | `src/MeshWeaver.Documentation/Data/GUI/` |
| AI Integration | `src/MeshWeaver.Documentation/Data/AI/` |
| Agent definitions | `src/MeshWeaver.AI/Data/Agent/` |

**Writing/editing a doc page:** follow [AuthoringDocumentation.md](src/MeshWeaver.Documentation/Data/Architecture/AuthoringDocumentation.md). Links resolve against the page's FULL node path at render time — sibling links need `../Sibling`, absolute links start `/Doc/…`; `xref:` and `.md` suffixes never resolve. `DocumentationLinkIntegrityTest` (test/MeshWeaver.Documentation.Test) fails on any broken internal link — run it after doc edits.

**Hub-handler test hangs or message disappears:** read [DebuggingMessageFlow.md](src/MeshWeaver.Documentation/Data/Architecture/DebuggingMessageFlow.md) first — it tells you which trace tags to grep and why you should never rerun a hung test "to see".

**`type 'X' is not registered in this hub's TypeRegistry`:** Fix is `WithType(typeof(X), nameof(X))` on the receiving hub. See DebuggingMessageFlow.md → "Type-registry mismatch".

**Use `hub.Observe(...)` not `RegisterCallback`/`AwaitResponse`** — those overloads are `[Obsolete]` and deadlock. Tests use `MonolithMeshTestBase.AwaitResponseAsync(...)`.

## Deployment

**Two deploy routes, different targets — neither deprecated.** Pick by target, don't mix:
- **AKS** — the shared cluster `memex` portal. Full ref: [DeploymentAKS.md](src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md).
- **Azure Container Apps** — the Aspire `test`/`prod` modes, via `tools/deploy.sh prod|test`. Full ref: [DeploymentContainerApps.md](src/MeshWeaver.Documentation/Data/Architecture/DeploymentContainerApps.md).

**🚨 Before any AKS deploy, read [DeploymentAKS.md](src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md) end-to-end** — it is the source of truth for build → roll-out → verify AND for the **auto-baked mesh-local `#r` package feed** (the `BakeMeshLocalFeed` target packs `MeshWeaver.BusinessRules` + `.Generator` into the image so scope/`IScope` nodes compile offline in prod — Release publish only, no manual pack step). The commands inlined below are a quick reference, not a substitute for the doc.

The `memex` portal runs on the shared **AKS cluster** `memexaks-cluster` (RG `memex-aks-rg`, swedencentral) — namespace `memex` — against the Postgres Flexible Server, images in ACR `meshweaver.azurecr.io`. **Private cluster: `kubectl` ONLY via `az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "…"`.**

**On AKS a code update = build image → set image → restart** (the AKS route does NOT use `tools/deploy.sh` or `aspire deploy` — those are the Container Apps route):

```bash
az acr login -n meshweaver
# Portal (custom base) AND migration (the migration is what creates schema + the matview):
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-portal-ai -p:ContainerImageTag=<tag> \
  -p:ContainerBaseImage=meshweaver.azurecr.io/memex-portal-ai-base:latest
dotnet publish memex/aspire/Memex.Database.Migration/Memex.Database.Migration.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-migration -p:ContainerImageTag=<tag>
# Roll out (NS = memex):
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "\
  kubectl -n <NS> set image deployment/memex-portal-deployment memex-portal=meshweaver.azurecr.io/memex-portal-ai:<tag>; \
  kubectl -n <NS> set image deployment/memex-migration-deployment memex-migration=meshweaver.azurecr.io/memex-migration:<tag>; \
  kubectl -n <NS> rollout restart deployment/memex-migration-deployment deployment/memex-portal-deployment; \
  kubectl -n <NS> rollout status deployment/memex-portal-deployment --timeout=300s"
```

- **`deploy/aks/envs/<env>/deploy.sh` is first-time ENV SETUP only** (helm install + PVCs + KV SecretProviderClass + ingress + connection-string patch). Do NOT use it for a code update — it re-runs the whole chart and can reset live config.
- **Don't run `tools/deploy.sh` or `aspire deploy` against the AKS cluster** — those are the *Container Apps* route (a different target), not a code-update path for AKS.
- `memex-migration` runs the migration then exits 0 and the Deployment restarts it (benign `CrashLoopBackOff`). Before declaring success, confirm its log shows `Database migration completed. Version: N` AND the portal serves (HTTP 200).

Routes + full reference: [Deployment.md](src/MeshWeaver.Documentation/Data/Architecture/Deployment.md) (index) · [DeploymentAKS.md](src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md) · [DeploymentContainerApps.md](src/MeshWeaver.Documentation/Data/Architecture/DeploymentContainerApps.md)

## Bash Command Guidelines

**Stay in root** (`C:\dev\MeshWeaver`). Avoid chained commands (`&&`, `||`), `for` loops, and `cd` — they all require user confirmation.

## Development Commands

```bash
dotnet build                                              # Build solution
dotnet test test/MeshWeaver.Data.Test --no-restore        # Run one test project
dotnet run --project memex/Memex.Portal.Monolith          # Monolith standalone (https://localhost:7122, http://localhost:5022)
dotnet run --project memex/aspire/Memex.AppHost           # Aspire (requires Docker) — portal at https://localhost:7202, http://localhost:5202
aspire run --project memex/aspire/Memex.AppHost           # Aspire via CLI (registers with `aspire mcp`) — same URLs as above
aspire start --no-build --project memex/aspire/Memex.AppHost  # Background + NO rebuild — fast bring-up; `aspire ps` / `aspire stop` to manage. --no-build reuses the last build (won't pick up source edits)
```

### Restarting just the Portal (no full Aspire restart)

When you change code in `Memex.Portal.Distributed` or any project it references, you do NOT need to kill the whole AppHost. Three options, ordered by cost:

1. **Hot reload (cheapest)** — start with `dotnet watch` instead of `dotnet run` / `aspire run`:
   ```bash
   dotnet watch --project memex/aspire/Memex.AppHost
   ```
   File save → Aspire restarts the affected resource only. Preserves the dashboard, the Postgres container, and the SignalR endpoints. Most code changes apply within seconds.
2. **Aspire dashboard UI** — open `https://localhost:17200/` → Resources tab → click the ⋯ next to `memex-portal-distributed` → **Restart**. Runs `dotnet build` + restart in-place.
3. **Process kill (last resort, when watch missed a change)**:
   ```powershell
   Get-Process Memex.Portal.Distributed -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
   Aspire's resource watcher detects the exit and restarts the resource within ~5 s. Avoids a full `aspire run` restart (which would also rebuild every other resource and re-launch Postgres / blob-storage containers).

**Don't** kill the whole `aspire` / `Memex.AppHost` process unless you changed AppHost wiring itself — full restart costs 30-60 s and loses the dashboard auth token.

Full reference: [LocalDevWorkflow.md](src/MeshWeaver.Documentation/Data/Architecture/LocalDevWorkflow.md)

## 🚨🚨🚨 ABSOLUTE: `GetMeshNodeStream().Update()` is the ONLY mutation API

**Every mesh-node mutation goes through `workspace.GetMeshNodeStream(path).Update(current => modified)`. There is no other mutation surface — do NOT invent one: no `SubmitMessageRequest`-style wire messages, no completion callbacks via `hub.Set<Action<...>>`, no bespoke `IRequest`/`IResponse` pairs for state changes. Migrate any straggler you touch to `stream.Update`.**

**Thread submissions** go through the canonical `IMessageHub` extension surface defined in `src/MeshWeaver.AI/HubThreadExtensions.cs`:

```csharp
hub.StartThread(namespacePath, userText, agentName: ..., contextPath: ..., onCreated: ..., onError: ...);
hub.SubmitMessage(threadPath, userText, agentName: ..., contextPath: ...);
hub.ResubmitMessage(threadPath, userMessageId, newUserText: ...);
hub.DeleteFromMessage(threadPath, atMessageId);
hub.MarkThreadDone(threadPath, done);
hub.RecordSubmissionFailure(threadPath, userMessageId, userText, errorMessage);
```

Every method writes the thread node via `hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(...)` (or `CreateNodeRequest` for new-thread lifecycle). The per-thread submission watcher reacts to the resulting state changes, drains `PendingUserMessages` into `Messages`, allocates cells, and invokes `ThreadExecution.ExecuteMessageAsync(execHub, RoundParams, AccessContext?)` **directly as a method** — no message dispatch. It returns `IObservable<Unit>`; the watcher **subscribes** and treats completion (gated on the terminal `Status` write) as round-done. **Tests, GUI, and agents all call these extensions — this is the complete submission surface; there is no other entry point.**

Full reference: [ThreadOperations.md](src/MeshWeaver.Documentation/Data/Architecture/ThreadOperations.md).

**Activity operations** go through the matching `IMessageHub` extensions in `src/MeshWeaver.Mesh.Contract/HubActivityExtensions.cs`:

```csharp
hub.CancelActivity(activityPath);                                  // RequestedStatus = Cancelled
hub.RequestActivityStatus(activityPath, ActivityStatus.Running);   // generic flip
```

Both write the activity node via `hub.GetWorkspace().GetMeshNodeStream(activityPath).Update(...)`; the activity hub's `WatchControlPlane` subscription reacts. Full reference: [ActivityOperations.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityOperations.md).

**Completion**: agent reaching terminal state writes `Status = Completed/Cancelled/Error` to the response cell via `PushToResponseMessage(...)` (stream.Update), AND creates a `Notification` MeshNode satellite at `{threadPath}/_Notification/{id}` via `EmitCompletionNotification`. The user's notification bell databinds to this — same source the tests assert on. Query shape: `path:{threadPath}/_Notification scope:children nodeType:Notification` (filter by nodeType for robustness when other satellite types live under the thread).

**Observing completion**: subscribe to `workspace.GetMeshNodeStream(path)` and wait for the relevant state on the node's `Content` (e.g. `MeshThread.Messages.Count >= 2`, `RequestedStatus = Cancelled`, `Status = Completed`). The GUI databinds the same way; tests do too.

**Tests**: any test that posts a verb-shaped request and waits for a response shape (`*Request → *Response`) is testing a deprecated API. Migrate to: write via `stream.Update`, observe via `GetMeshNodeStream(path).Where(node => predicate).FirstAsync().Timeout(...)`.

**Application code uses only `stream.Update`.** Internal plumbing that `stream.Update` itself uses (`PatchDataRequest` for cross-hub writes, `DataChangedEvent` for stream fan-out) is fine where it already exists — but you never `hub.Post(PatchDataRequest, ...)` from application code. If you find yourself doing that, you're bypassing the API; use `workspace.GetMeshNodeStream(path).Update(...)` and the framework posts the patch for you.

### Updating an external node — `GetMeshNodeStream(path).Update(...)`

The same API works for nodes the caller does NOT own. `workspace.GetMeshNodeStream(path)` returns a handle that auto-dispatches:

- `path == my-hub's-address`: writes go through the local data source (`UpdateOwn`).
- `path != my-hub's-address`: writes route to the owning per-node hub via the process-wide `IMeshNodeStreamCache`, which opens a sync subscription + posts a JSON-merge `PatchDataRequest` (RFC 7396) to that hub. The owner serialises every mirror's write through its single-threaded action block — no race, no clobber.

```csharp
// Own node (this hub) — Update is COLD: the trailing Subscribe runs the write.
workspace.GetMeshNodeStream().Update(node => node with { Content = ... })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));

// External node (anywhere in the mesh — same API):
workspace.GetMeshNodeStream(otherPath).Update(node => node with { Content = ... })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));
```

The remote variant returns the locally-computed updated snapshot optimistically; if you need the owner's reconciled state, take the next emission off the same `GetMeshNodeStream(path)` handle.

**Eventual-consistency safe**: cross-hub `stream.Update` does NOT send the whole node back. It diffs `current` vs `update(current)` and sends only the RFC 7396 JSON-merge patch. The owner merges the patch against its CURRENT state, so concurrent writers from different mirrors don't clobber each other's fields (Mirror A's `{Content: {Field1: X}}` and Mirror B's `{Content: {Field2: Y}}` both land — never "last write wins on whole node"). Treat your `update` lambda accordingly: touch only the fields you intend to change.

### The 3 rules (unchanged)

This is the unification of three rules we used to write separately:

1. **Writes**: `stream.Update(current => current with { Content = ... })`. The owning hub's action block serialises; no race. State-machine semantics? Set a `RequestedX` field — the owning hub's watcher reacts (see [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md)).
2. **Reads**: `workspace.GetMeshNodeStream(path)` / `Hub.GetMeshNodeStream(path)` — server-side AND Blazor, backed by the process-wide [IMeshNodeStreamCache](src/MeshWeaver.Hosting/MeshNodeStreamCache.cs) (one shared handle per path; see [GUI Data Binding](src/MeshWeaver.Documentation/Data/GUI/DataBinding.md)). `GetRemoteStream<MeshNode, …>` is framework plumbing — never use it for a node by path. Never `meshService.QueryAsync(path:X)` for a single node's content (stale by design).
3. **Delete the request type.** If you find yourself writing `class XxxRequest` to mutate a thread / message / NodeType, stop. Add a `RequestedXxx` field to the node's content and watch it from the owning hub.

Sanctioned exceptions (NOT for state mutations):
- `CreateNodeRequest` / `DeleteNodeRequest` / `MoveNodeRequest` — node-lifecycle on the mesh hub. These route, they don't mutate node content.
- Transient queries that don't belong on any node (e.g. autocomplete completions).

Why this rule unblocks tests: every "hub becomes unresponsive after the second compile" failure (CodeEditRecompile, NodeTypeRelease, LinkedInPullActions, ThreadAgentIntegration in CI 26036857424) traces back to bespoke request/response patterns that race the watcher → two concurrent activities → leaked callbacks → wedged hub.

Canonical references:
- [MeshNodeStreamCache.md](src/MeshWeaver.Documentation/Data/Architecture/MeshNodeStreamCache.md) — the handle contract: one cache per silo, one shared handle per path, serial write queue, storm breaker.
- [RequestViaStreamUpdate.md](src/MeshWeaver.Documentation/Data/Architecture/RequestViaStreamUpdate.md) — the canonical pattern + helpers (`hub.WatchControlPlane`, `hub.WatchSubmission`).
- [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md) — `Status`/`RequestedStatus` pair, operations-as-scripts.
- [CqrsAndContentAccess.md](src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md) — read semantics + why `QueryAsync` lags.
- [DataBinding.md](src/MeshWeaver.Documentation/Data/GUI/DataBinding.md) — the Blazor-side mirror of the same pattern.

## 🚨 Never write as hub — AccessContext propagation

**Every framework write primitive (`meshService.CreateNode/UpdateNode/DeleteNode/CopyNode`, `MeshNodeStreamHandle.Update`, `IMeshNodeStreamCache.Update`) automatically carries the caller's `AccessContext` through `.Subscribe()` boundaries.** Callers keep writing the natural `.Subscribe(...)` shape; the framework guarantees the operation runs under the calling user's identity even when the emission lands on another thread.

If a write must run as system/hub (legitimate infrastructure — cache hydration, SyncStream heartbeats), wrap explicitly:
- `using (accessService.ImpersonateAsSystem()) { … }` — well-known `"system-security"` identity; `Permission.All` granted unconditionally.
- `using (accessService.ImpersonateAsHub(hub)) { … }` or `o.ImpersonateAsHub(hub.Address)` on the post — stamps the hub's address as principal.

PostPipeline fails closed when no context is set. The "silently stamp hub-self as principal" fallback was deleted 2026-05-21 — it masked the prod EventCalendar bug. Application code that needs to write MUST have a real user identity on `AccessService.Context` (set by MessageHub on every handler invocation from `delivery.AccessContext`).

Canonical reference: [AccessContextPropagation.md](src/MeshWeaver.Documentation/Data/Architecture/AccessContextPropagation.md).

## 🚨🚨🚨 ABSOLUTE: Nothing async, EVER — *NO* `async`, *NO* `await`, *NO* `Task<T>` in hub/UI code

**The user is LITERALLY NEVER OK with `async`/`await`/`Task<T>`/`.ToTask()`/`TaskCompletionSource` in any hub-reachable OR Blazor-view/component code.** It runs continuations on the wrong scheduler, deadlocks the single-threaded action block, and (the 2026-06-10 chat regression) NotFound-storms a partition hub until the whole portal wedges. **Read [AsynchronousCalls.md](src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md) BEFORE writing any call that touches the hub, a mesh node, or a stream.**

Everything is `IObservable<T>` end-to-end — compose and **`.Subscribe(...)`**, never `await`:
- **Create / read / update a node** → `meshService.CreateNode(node).Subscribe(...)` · `hub.Observe<TResp>(req).Subscribe(...)` · `workspace.GetMeshNodeStream(path).Update(cur => …).Subscribe(...)`. NEVER `await …Async()`. (For create-or-update use the reactive `hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)).Subscribe(...)` — see `StaticRepoImporter` / `NodeCopyHelper`.)
- Handlers, services, layout areas → return `IObservable<T>` (or `void` for fire-and-forget). Never `Task<T>`.
- Compose with `.SelectMany`, `.Select`, `.Where`, `.Timeout`. Chain dependent work in `.SelectMany`, not `await`.
- Click actions: `WithClickAction(ctx => { ...; return Task.CompletedTask; })` — never `async ctx =>`.
- `async`/`await`/`Task.Run`/`TaskCompletionSource`/`.ToTask()`/`.Result`/`.Wait()` in hub or Blazor-view code = red flag — delete it, return/compose `IObservable<T>` and Subscribe.
- **Tests only**: `await .FirstAsync().ToTask()` is acceptable. Nowhere else.

### 🚨🚨🚨 ABSOLUTE: `Observable.FromAsync` is NEVER tolerated

**Writing `Observable.FromAsync(...)` anywhere in `src/` is FORBIDDEN — no exceptions, no "Postgres is special", no "storage is the hot path".** A bare `FromAsync` runs the function's synchronous prologue on the **subscribing thread** (the hub/grain scheduler when the subscribe happens mid-handler) and applies no concurrency bound — the exact deadlock-and-exhaustion bug class the I/O pool exists to kill. There is exactly **one** place `FromAsync` may appear: sealed *inside* `IoPool` itself. Everywhere else it is a defect.

**Every async / blocking / IO edge goes through `IIoPool`** (`MeshWeaver.Mesh.Threading`), resolved from `IoPoolRegistry` (mesh-scoped singleton — never static):

| You have | Use |
|---|---|
| A `Task<T>`-returning leaf (DB round-trip, blob, HTTP, async file) | `pool.Invoke(ct => SomethingAsync(ct))` — or `pool.Run(...)` for the eager **promise-cache** (ReplaySubject-backed: runs once, replays to all) |
| A sync-blocking / CPU leaf (`File.ReadAllBytes`, Roslyn compile, `Process`) | `pool.InvokeBlocking(ct => Work(ct))` |
| An `IAsyncEnumerable<T>` leaf | `pool.InvokeStream(...)` / `pool.RunStream(...)` |

**The promise-cache pattern (idempotent one-shots like schema provisioning):** cache the `pool.Run(...)` observable in an *instance* `ConcurrentDictionary<key, IObservable<T>>` (never static) — the first caller kicks the work off on the pool, every later subscriber replays the cached completion. Canonical: `PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned` (`_provisioned.GetOrAdd(schema, _ => _ioPool.Run(ct => EnsureSchemaAsync(def, ct)))`). PG pools are named `pg:{adapter}` and capped at **1** so the gate *is* the single Npgsql connection.

- **Public surface returns `IObservable<T>`, never `Task<T>`.** A `Task`-returning method that does IO is the smell; rewrite it to return `IObservable<T>` and bridge the leaf through `IIoPool` internally.
- **MCP/SDK surface adapters**: one-line `public Task<string> Patch(...) => ops.Patch(...).FirstAsync().ToTask();` is the only place `Task` appears at the boundary — and even there the body is reactive.
- Full reference: [ControlledIoPooling.md](src/MeshWeaver.Documentation/Data/Architecture/ControlledIoPooling.md).

**🚨 Cold observables: Subscribe is mandatory.** Every method that performs a write returns a cold `IObservable<T>` — the side effect runs on `Subscribe`, not on call. Forgetting to subscribe means the work silently doesn't happen.

```csharp
// ❌ WRONG — fire-and-forget. UpdateMeshNode is cold; the dsStream.Update side
//   effect only runs on Subscribe. This was the chat-doesn't-work root cause.
workspace.GetMeshNodeStream().Update(node => node with { … });

// ✅ RIGHT — subscribe with explicit error propagation.
workspace.GetMeshNodeStream().Update(node => node with { … })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "Update failed for {Path}", path));
```

`workspace.GetMeshNodeStream()` returns a `MeshNodeStreamHandle` that is both `IObservable<MeshNode>` (read) AND has `.Update(update)` (write). Writes return `RequireSubscribeObservable<MeshNode>` which **logs a warning at GC if Subscribe was never called** — search the `MeshWeaver.Mesh.RequireSubscribe` log channel after every CI run. Old API `workspace.UpdateMeshNode(...)` is `[Obsolete]`.

**Auto-save pattern:** Form fields update the MeshNode via `stream.UpdateMeshNode` (debounced). The click action reads nothing — just flips a trigger field. No `Take(1)` on a hot stream.

Full patterns + mistake ledger: [AsynchronousCalls.md](src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md)

## 🚨 CQRS — Never Query for a Single Node's Content

`QueryAsync`/`ObserveQuery` are eventually consistent — **stale after writes**. To read a specific node:

```csharp
// ❌ WRONG — lagged index, stale after writes
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();

// ✅ CORRECT — authoritative, live (shared IMeshNodeStreamCache handle)
workspace.GetMeshNodeStream(path)
    .Where(node => node is not null)
    .Take(1).Timeout(TimeSpan.FromSeconds(10));
```

**Valid query uses:** listing children (`path/*`), searching by predicate, existence checks, autocomplete.  
**Wrong:** reading content by exact path, reading state before a write, polling for job completion.

`GetMeshNodeStream(path)` + `Where(...).Take(1)` is also the right primitive for **waiting for work to finish**.

**Free-floating words → vector search.** When a query contains bare text tokens (`laptop nodeType:Story`) AND PG is the backend AND an `IEmbeddingProvider` is registered, `PostgreSqlMeshQuery.QueryAsync` automatically routes through the HNSW cosine index instead of ILIKE substring scan. Structured-only queries (`nodeType:Story namespace:ACME`) stay on the regular SQL path. Full reference: [VectorSearch.md](src/MeshWeaver.Documentation/Data/Architecture/VectorSearch.md).

Full treatment: [CqrsAndContentAccess.md](src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md)

## Mesh URL Shape

`{baseUrl}/{meshpath}` — no `/node/` segment, no URL-encoding of separators.

| Environment | Base URL |
|---|---|
| Prod | `https://memex.meshweaver.cloud` |
| Dev — Aspire (`memex/aspire/Memex.AppHost`) | `https://localhost:7202` (HTTP fallback `http://localhost:5202`) |
| Dev — Monolith standalone (`memex/Memex.Portal.Monolith`) | `https://localhost:7122` (HTTP fallback `http://localhost:5022`) |

## `@/` is Local-Only

`@/path` is a Unified Content Reference for markdown links (`[text](@/Path)`), autocomplete, and agent tool args — **never in `href=""` attributes or HTTP URLs**. Markdig strips `@` in native markdown syntax but NOT inside `<a href>`.

## 🚨🚨🚨 ABSOLUTE: No static collections — ever

**A `static` field that is a collection or cache is FORBIDDEN** anywhere in `src/` or `test/`: no `static ConcurrentDictionary`, `static Dictionary`, `static HashSet`, `static List`, `static ConcurrentBag`/`Queue`, `static MemoryCache`/`IMemoryCache`, `[ThreadStatic]`, or `static Lazy<…>` of mutable data. Process-wide static state survives mesh disposal, so it **bleeds across tests** — the moment you add a `Clear()` method "for test isolation", that method *is* the proof of the bug — and across users/partitions in prod.

**Every cache and every repository is an instance owned by the mesh.** Register it in `MeshBuilder` (`ConfigureServices` / `WithServices`) as a **singleton** so its lifetime IS the mesh's: when the mesh hub is disposed (end of test / shutdown), the cache dies with it. Hold the backing store (`IMemoryCache`, an instance `ConcurrentDictionary`, …) as an **instance field** on that singleton; resolve via `hub.ServiceProvider.GetRequiredService<T>()`.

```csharp
// ❌ FORBIDDEN — process-wide, survives mesh disposal, bleeds across tests
public static class NodeTypeRegistry
{
    private static readonly ConcurrentDictionary<string, MeshNode> Nodes = new();
    public static void Clear() => Nodes.Clear();   // ← "for test isolation" = the tell
}

// ✅ REQUIRED — instance repo, registered in MeshBuilder, lifetime = mesh
public sealed class NodeTypeRepository
{
    private readonly ConcurrentDictionary<string, MeshNode> nodes = new();   // instance, not static
    public void Register(MeshNode node) => nodes[node.Path] = node;
    public bool TryGet(string path, out MeshNode? node) => nodes.TryGetValue(path, out node);
}
builder.ConfigureServices(s => s.AddSingleton<NodeTypeRepository>());          // dies with the mesh — no Clear() needed
```

**Allowed `static readonly`:** immutable, read-only constant lookups initialized once and never written at runtime (media-type maps, reserved-word sets, role tables). If it never takes a write after construction it's a *constant*, not a cache — fine. The instant something writes to it at runtime, it must become a mesh-scoped instance singleton.

Full reference: [NoStaticState.md](src/MeshWeaver.Documentation/Data/Architecture/NoStaticState.md).

## Collections Policy

**NEVER use mutable collections.** Always `System.Collections.Immutable`:  
`List<T>` → `ImmutableList<T>`, `Dictionary<K,V>` → `ImmutableDictionary<K,V>`, `HashSet<T>` → `ImmutableHashSet<T>`, `Queue<T>` → `ImmutableQueue<T>`.  
Exception: `ConcurrentDictionary` for concurrent mutation — **as an instance field on a mesh-scoped singleton, never `static`** (see "No static collections" above).

## Architecture Overview

Actor-model message hub (`MeshWeaver.Messaging.Hub`) with address-based partitioning. UI is reactive Layout Areas rendered in Blazor Server. AI agents use plugins (MeshPlugin, LayoutAreaPlugin).

| Directory | Contents |
|---|---|
| `src/` | Core framework (50+ projects) |
| `samples/Graph/Data/` | Sample data nodes (ACME, Northwind, Cornerstone, etc.) |
| `memex/Memex.Portal.Monolith/` | Dev portal with full Graph + Documentation support |
| `memex/aspire/` | Microservices with .NET Aspire orchestration |

**Request-Response:** `hub.Observe<TResponse>(request, o => o.WithTarget(address)).Subscribe(resp => …, ex => …)`  
Response sent as: `hub.Post(responseMessage, o => o.ResponseFor(request))`  
**Fire-and-Forget:** `hub.Post(message, o => o.WithTarget(address))`  
**Layout area route:** `@{address}/{areaName}/{areaId}`

## Data Access Patterns

Never use `IMeshStorage` or `IMeshCatalog` directly — internal infrastructure only.

| Operation | API |
|---|---|
| Read (query) | `IMeshService.QueryAsync(...)` |
| Read (single node) | `workspace.GetMeshNodeStream(path)` |
| Create/Delete | `meshService.CreateNode(node).Subscribe(...)` / `meshService.DeleteNode(path).Subscribe(...)` |
| Update | `workspace.GetMeshNodeStream(path).Update(current => current with { … })` |
| Move | `hub.Observe(new MoveNodeRequest(src, dst)).Subscribe(...)` |

Always `GetRequiredService<T>()` — never `GetService<T>()` + null check for required services.

Full reference: [DataAccessPatterns.md](src/MeshWeaver.Documentation/Data/Architecture/DataAccessPatterns.md)

## Memex is available through MCP

The memex mesh is reachable through the **`meshweaver` MCP server** — for agents working on this repo (the `atioz` / `memex-systemorph` MCP tools you already have) AND for the co-hosted **Claude Code / GitHub Copilot** harnesses, which get a per-user `meshweaver` HTTP MCP server wired **automatically** (authenticated as the calling user). The mesh — NOT a local file tree — is the workspace: use the MCP tools to read/modify mesh content rather than guessing — `get` / `search` (read), `create` / `update` / `patch` / `move` / `copy` / `delete` (mutate), `execute_script`, `render_area`, `navigate_to`, `upload`. This file (`AGENTS.md`, read by both Claude Code and Copilot) is the canonical place that tells the co-hosted agents the mesh is MCP-accessible.

## MCP Mutations — Always Show a Diff

For every MCP mutation (`patch`, `update`, `create`, `delete`, `move`, `copy`):
1. `get @path` **before** — cache the JSON
2. Mutate
3. `get @path` **after** — cache the new JSON
4. Render a ` ```diff ` block showing the changed region in your response

Read-only tools skip this: `get`, `search`, `recycle`, `get_diagnostics`, `navigate_to`, `execute_script`.

## Development Patterns

For detailed patterns with code examples, read:
- Layout areas + UI controls: [UserInterface.md](src/MeshWeaver.Documentation/Data/Architecture/UserInterface.md) and [GUI docs](src/MeshWeaver.Documentation/Data/GUI/)
- Message handling: [MessageBasedCommunication.md](src/MeshWeaver.Documentation/Data/Architecture/MessageBasedCommunication.md)
- AI plugins: [AI docs](src/MeshWeaver.Documentation/Data/AI/)
- Activity control plane / operations as scripts: [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md)
- Reactive click handlers + service patterns: [AsynchronousCalls.md](src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md)

**Static handlers for one-shot pipelines** — don't extract `IFooService` for DI cleanliness when there's no state. Resolve deps via `hub.ServiceProvider.GetRequiredService<T>()` inside the static handler.

**Operations with inputs + progress + output** (export, import, compile, mirror) → Code MeshNode template + form-bound inputs + `RequestedStatus = Running` trigger. Not a bespoke `XxxRequest/XxxResponse` handler. See [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md).

## Key Dependencies

.NET 10.0 · Orleans · Blazor Server · Microsoft.Extensions.AI · xUnit v3 · FluentAssertions · Markdig · Chart.js · Azure SDKs

## Testing Guidelines

Before building NodeTypes, data models, layout areas, or CSV loaders — read [Coder.md](src/MeshWeaver.AI/Data/Agent/Coder.md) first (canonical guide + non-negotiable testing standards).

**No mocking.** Use `MonolithMeshTestBase` or `OrleansTestBase` — never mock `IMessageHub`, `IMeshService`, or core interfaces.  
**Always `run_in_background: true`** for test runs (they take minutes).  
**Never `--verbosity minimal`** when tests may fail — it hides stack traces.

**Never `Task.Delay` to wait for propagation.** A fixed sleep races CI load: too short → flakes, too long → wastes minutes across the suite. Wait on the actual condition via `stream.Where(...).FirstAsync().Timeout(...)`. When the source is request/response (not an observable), wrap the re-query in `Observable.Interval(50.Milliseconds()).StartWith(0L).SelectMany(...).Where(predicate).FirstAsync().Timeout(...)`. Hand-rolled `while + Task.Delay(50)` poll loops are forbidden. Sanctioned `Task.Delay` uses: forcing distinct timestamps for sort assertions, and "wait to confirm nothing happened" negative tests where there's no positive signal to filter for. See WritingTests.md → "Polling loops around QueryAsync" for the full pattern.

**Never assert "exactly N change events"** on a stream backed by pg_notify or any change feed that can race the initial-snapshot path. Filter on the emission shape (e.g. `.Where(c => c.ChangeType == QueryChangeType.Initial)`), not the count.

xUnit v3 config (`xunit.runner.json`): `parallelizeAssembly: false`, `maxParallelThreads: 1`, `methodTimeout: 60000ms`.

Full guidance: [WritingTests.md](src/MeshWeaver.Documentation/Data/Architecture/WritingTests.md)

### Running Tests

```bash
dotnet test test/MeshWeaver.Hosting.Monolith.Test --no-restore
dotnet test test/MeshWeaver.Graph.Test --filter "ClassName~AccessAssignment" --no-restore
```

Workflow: run once in background → read failures → fix → run once more. **🚨 NEVER re-run a test (single or suite) unless code under test has changed.** Re-running to "see if it was a flake" hides the bug — flakes are real races. Either fix the race or pin the failure with a smaller repro; do not retry. The only exceptions: (a) the test harness itself crashed (MSBuild MSB4166, infrastructure error — re-run is the same input), (b) the previous run was killed by the user before completion.

### DevLogin and Access Control

`MonolithMeshTestBase` auto-logs in `rbuergi@systemorph.com` as Admin. Available helpers: `TestUsers.Admin`, `TestUsers.SampleUsers()`, `builder.AddSampleUsers()`.

For per-user access control tests, use `accessService.SetCircuitContext(new AccessContext { ObjectId = "...", Name = "..." })` before creating test data; set `null` after.

### Node Types

Standard types from `AddGraph()`: `Markdown`, `Code`, `Agent`, `Group`, `User`, `VUser`, `Role`, `Notification`, `Approval`, `AccessAssignment`, `GroupMembership`, `PartitionAccessPolicy`, `ActivityLog`, `UserActivity`, `Comment`, `Thread`, `ThreadMessage`

Custom types: `builder.AddMeshNodes(new MeshNode("MyType") { Name = "My Type" })` in `ConfigureMesh`.

### Test Base Classes

- **`MonolithMeshTestBase`** (recommended) — full integration with persistence, messaging, DI; use `AwaitResponseAsync(request, ...)` for request/response in tests
- **`HubTestBase`** — message routing / layout tests; bridge to Task via `.FirstAsync().ToTask(ct)`

For satellite entities (comments, threads, tracked changes): [SatelliteEntityPatterns.md](src/MeshWeaver.Documentation/Data/Architecture/SatelliteEntityPatterns.md)

## Project Structure

Framework code in `src/`, tests in `test/`, samples in `samples/`.  
Main branch: `main`. Solution file: `MeshWeaver.slnx` (50+ projects).  
Package management: `Directory.Packages.props` — update this, not individual `.csproj` files.
