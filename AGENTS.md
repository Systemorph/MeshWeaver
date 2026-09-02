# AGENTS.md

Guidance for AI agents working with this repository.

**This file states the rules; the evidence lives in skills.** Every section is an imperative you must
obey. Where one ends in `Full reference: [/name]`, the skill under `.claude/skills/<name>/` carries
the worked examples, commands, war stories and incident history — load it when the task calls for
it, not before.

| Skill | Load it when |
|---|---|
| [/worktree](.claude/skills/worktree/SKILL.md) | starting any change: branch, edit, build, push |
| [/pullrequest](.claude/skills/pullrequest/SKILL.md) | opening / reviewing / merging a PR |
| [/ci](.claude/skills/ci/SKILL.md) | authoring a workflow, a gate, satellite-repo CI |
| [/release](.claude/skills/release/SKILL.md) | shipping, tagging, checking what actually published |
| [/deployment](.claude/skills/deployment/SKILL.md) | rolling a portal, patching an environment |
| [/testing](.claude/skills/testing/SKILL.md) | writing or running a test, triaging a red run |
| [/mesh-data](.claude/skills/mesh-data/SKILL.md) | any node read/write, query, payload cast, partition schema |
| [/async](.claude/skills/async/SKILL.md) | any hub-reachable or Blazor-view call; IO; AccessContext |
| [/gui](.claude/skills/gui/SKILL.md) | any layout area, control, editor, form, table |
| [/i18n](.claude/skills/i18n/SKILL.md) | any string a human reads on screen |
| [/debug](.claude/skills/debug/SKILL.md) · [/storm](.claude/skills/storm/SKILL.md) · [/sigsegv](.claude/skills/sigsegv/SKILL.md) | a hang/timeout; a restart/502/log flood; a crashed host |

## Git Workflow

**When the task's goal is reached, automatically follow the [`pullrequest` skill](.claude/skills/pullrequest/SKILL.md)** — What's New entry (`Category: Fix` for a bug fix a user can notice, `Category: Feature` otherwise — fixes are the entries that go missing), commit, push, open the PR, wait for green CI, merge. "Goal reached" means implemented, verified, and the touched projects build clean with CI's flags; don't stop to ask permission at that point. Everything short of that stays manual: never commit or push half-done or unverified work, and **never merge with CI red or pending**.

### 🚨🚨🚨 ABSOLUTE: NEVER work on the primary checkout — it stays on `main`, untouched. EVERYONE creates a worktree.

**The primary checkout (`/Users/roland/code/MeshWeaver`) is a READ-ONLY reference, not a workspace. It must stay parked on `main` and untouched — never edit, build, commit, `checkout`, `switch`, `reset`, or `stash` there, and never leave it on a feature branch.** It is the base every session's worktree is cut from; mutating it can clobber every concurrent session's uncommitted WIP.

**EVERY agent session — no exceptions — works in its OWN `git worktree` on a fresh branch, and does ALL edits, builds, commits, and pushes there** (`git worktree add -b feat/x /Users/roland/code/MW-x origin/main`). About to touch a file under the primary? STOP and create a worktree first. **Never `git stash`** — the stash stack is repo-global and collides across worktrees; use `git diff > patch` + `git apply`. Parallel PR-building sub-agents must pass `isolation: "worktree"` as a tool PARAM (a prompt-only instruction does nothing).

### 🚨 Before you push: make CI green LOCALLY first

CI builds **Release with warnings-as-errors**; a plain local Debug build passes while CI fails. **Build every touched project and its dependents with `dotnet build -c Release -warnaserror`, one project per invocation, and only push when that is clean.** The bar is MERGEABLE, not merged: a branch merely *behind* main merges fine here (`strict: false`, one required check — `Consolidate test results`), so do NOT re-sync just to catch up; merge main only when the PR is `DIRTY` or CI fails on something your diff cannot reach. 🚨 `strict` is **PER-REPO** and it FLIPS — `MeshWeaver.Plugins` was `strict: true` until 2026-08-29 and measured `false` on 2026-09-02; never trust a written value, run `gh api repos/Systemorph/<repo>/branches/main/protection --jq '.required_status_checks.strict'`.

### 🚨 A verification step that cannot fail is not a verification step

**Demand a positive, specific success signal — `0 Error(s)`, a `.trx` that exists, an elapsed time that makes sense — never "the command returned".** Each of these has produced a *false pass* here: `timeout`/`gtimeout` (no such binary on this macOS host — the wrapped command never runs), `--no-build`/`--no-restore` on a project this worktree never built (exit 0, no output, no `.trx`), several project args to one `dotnet build` (MSB1008), piping a build into `tail` (the exit code becomes the pager's), a 2-second "Build succeeded" (up-to-date no-op), `--no-build` after editing an embedded doc asset, and reading a background task's output right after launching it. Cap a local run by backgrounding it and holding your own deadline against `date -u`. Over budget means **stuck** — find what is not completing, never raise the bound. Full reference: [/worktree](.claude/skills/worktree/SKILL.md).

## 🚨🚨🚨 ABSOLUTE: Green CI does NOT mean the mesh compiles — in-mesh source is invisible to `dotnet build`

**Every `.cs` stored in a mesh node — NodeType `Source/*.cs`, Scripts, layout areas — compiles at RUNTIME in the portal, NEVER in CI**, and a NodeType's `configuration` lambda is C# inside a JSON string, invisible to `grep --include='*.cs'` as well.

- **Deleting or renaming ANY public framework surface is a breaking change to code the compiler cannot see.** Before deleting one, search the node trees (`samples/*/Data`, every node repo's content) **and the node JSON**, and search the live mesh (`search_chunks`) — it may hold callers the repo has already dropped. 🚨 **A `search_chunks` answer carrying `"searched": false` is a FAILED sweep, not a clean one** (#2741): the deployment has no embedding provider, nothing was searched, and the envelope deliberately carries no `count` so an absent field cannot be read as "no callers". Sweep on another deployment or stop. Port or delete callers in the SAME change; a clean `-warnaserror` build proves nothing here.
- **Before prod, sweep every NodeType green** (`Search('nodeType:NodeType')` → `LspDiagnosticsForNode` → fix roots first → re-sweep). 🚨 `ok:false` with a `status` other than `Compiled` (`Absent`/`NotCompilable`/`Unavailable`) is a sweep FAILURE, not a pass — that entry was never checked. Warnings count: `stayed an untyped JsonElement` means a view renders empty.

Full reference: [/ci](.claude/skills/ci/SKILL.md) · [NodeTypeCompilation.md](src/MeshWeaver.Documentation/Data/Architecture/NodeTypeCompilation.md).

## 🚨🚨🚨 ABSOLUTE: No band-aids — root cause only, literally always

**The user is LITERALLY NEVER interested in a band-aid, workaround, mitigation, or symptom-suppression.** When something hangs, deadlocks, flakes, or errors, find the EXACT defect and fix THAT. These are band-aids, and proposing one as "the fix" is forbidden:

- **Increasing a bound to make it pass** — pool size, timeout, retry count, buffer size, `maxParallelThreads`. The question is never "how do I get more headroom", it is "why is the slot/thread/budget not released, or why is it erroring".
- **A watchdog / timer / poller that resubscribes or retries** to recover from a state that "shouldn't happen". If the initial state never arrives, find why it is dropped or erroring.
- **`catch {}` / swallow-and-continue / `.Catch(Observable.Empty)`** that hides a fault instead of surfacing or fixing it.
- **Revert-and-move-on** when the revert just hides a defect that is still live underneath.
- **A `Clear()` for test isolation, a widened `.Timeout(...)`, a sleep** — each is the *tell* of an unfixed root cause.

If active bleeding genuinely needs a stopgap first, say so EXPLICITLY ("this is a temporary stopgap; the root cause is X; I will fix X") — then fix X. Default to a **deterministic repro** that pins the true cause before changing code. Full reference: memory `feedback_no_bandaids`.

## 🚨🚨🚨 ABSOLUTE: No hand-woven async/concurrency primitives — the actor model does NOT tolerate `SemaphoreSlim`

**A `SemaphoreSlim` — or any hand-rolled async gate, lock-for-async, or signal (`TaskCompletionSource` as a gate, a `Task.Delay` timeout race, `ManualResetEventSlim`, `lock`-around-`await`) — anywhere in `src/` **or `test/`** is FORBIDDEN, outside the one place sealed inside `IoPool`.** It parks the single-threaded action block (or grain turn), so the message you are waiting on can never be processed → deadlock. **Serialization channels through the hub** (a `Subject<T>` + `.Select(Run).Concat().Subscribe(...)`, or `GetMeshNodeStream(path).Update(...)`), and **concurrency bounding / one-shot init channels through `IIoPool`** — `pool.Run(...)` held in an *instance* `PromiseCache`/`PromiseSlot`, never a `SemaphoreSlim(1,1)` and never a bare `ConcurrentDictionary<key, IObservable<T>>` (a ReplaySubject latches `OnError` and replays one transient fault forever, #1369).

🚨 **`test/` reached ZERO on 2026-08-30 and the allow file is DELETED** — every root is now scanned with no escape hatch. In a test the two shapes are: a producer→test signal is an `AsyncSubject<Unit>` the producer completes, awaited through the assertion helpers (`await x.Should().Within(...).Emit(because)` / `.NotEmit(within)`) — **never `.Wait()` on it**, which only trips `BlockingBridgeInTestRatchetGuard` instead; and a release INTO a worker the test deliberately parks (the park being the subject) is a volatile `int` polled under a bounded `SpinWait.SpinUntil`, written in a **`finally`** so a failing assertion cannot strand it.

Full reference: [/async](.claude/skills/async/SKILL.md) · [RemovingHandWovenGates.md](src/MeshWeaver.Documentation/Data/Architecture/RemovingHandWovenGates.md) · [ControlledIoPooling.md](src/MeshWeaver.Documentation/Data/Architecture/ControlledIoPooling.md) · memory `feedback_no_semaphoreslim`.

## 🚨🚨🚨 ABSOLUTE: Never hand-roll UI / data-binding / persistence / submit — use the framework

**A "UI feature" means wiring up the framework's EXISTING pieces, never reinventing them.** Before writing ANY UI/binding/persistence code, FIND the existing area/control/macro/extension and use it; if you are reaching for `GetDataStream`/`Subscribe`/`Update`/`CombineLatest`/a new wrapper for a UI feature, STOP.

- **Editing a node's content** → bind the GUI DIRECTLY to the node stream (`MeshNodeContentEditorControl.ForType`, `MarkdownEditorControl.WithAutoSave`, `MeshNodePickerControl`). 🚨 **NEVER replicate the node into a layout-area `/data/{id}` copy plus a save subscription** — any `*AutoSave` helper, or a "Save" button that reads `/data` and writes the node, is the forbidden replicate-then-save antipattern (two stores drift; the save loop clobbers unedited fields).
- **Tabular / structured data → a framework CONTROL, NEVER hand-built HTML.** `Controls.DataGrid` + `PropertyColumnControl<T>`, composed with `Controls.Stack`/`LayoutGrid`/`Title`/`Markdown`. **FORBIDDEN:** `StringBuilder`/`$"<table>…"`, any `RenderHtml`-shaped helper, or `Controls.Html(handBuiltMarkup)` for structured data.
- **Form controls** → the `Edit` macro + `[UiControl<T>]`/`[Description]`/`[Editable(false)]`; no hand-built selects/checkboxes/textareas + a data section. **Submitting a chat message** → the existing `hub.StartThread(...)` / `hub.SubmitMessage(...)` extensions; no wrapper class, no path→id resolution.
- **Never** `.Take(1)` on a stream feeding a live data-bound view — it freezes the binding.

Full reference: [/gui](.claude/skills/gui/SKILL.md) · [GUI/DataBinding.md](src/MeshWeaver.Documentation/Data/GUI/DataBinding.md) · memory `feedback_no_handrolling`.

## 🚨🚨🚨 ABSOLUTE: Never change log levels in code for debug reasons

**Editing `LogInformation` ↔ `LogDebug` ↔ `LogTrace` (or `appsettings.json` under `src/`) to dial verbosity for a debugging session is FORBIDDEN** — log levels reflect the production cost model, and `Information` lines ship to Loki. To turn the volume up, edit the appsettings.json in the test's `bin/Debug/net10.0/` (`reloadOnChange: true` flips it mid-run). The src-tree `appsettings.json` and every `Log*` call in `src/` is committed contract; a genuinely mis-levelled call gets a real commit explaining the cost/value trade-off.

## 🚨🚨🚨 ABSOLUTE: A gate NEVER tests its own inputs — no skip-trapdoors

**A CI gate must never carry `continue-on-error:` on the step that fetches its input, nor an `if:` that asks whether a secret/variable is set.** GitHub paints a skipped job the same colour as a passed one, so "the gate never ran" and "the gate passed" become indistinguishable. Instead: one `preflight` job asserts every external input and fails RED naming what to provision; gates `needs:` it and run unconditionally; the fork-PR exemption is expressed once, on the *event*, never as "the secret is empty"; the required check carries `preflight` in `needs` plus an explicit fail step. The same applies to guard TESTS — a guard whose subject moved and whose roots did not passes having checked nothing.

**And never hand-roll (or copy-paste) a node repo's CI.** The shared jobs live here as `workflow_call` workflows (`.github/workflows/node-repo-{validate,compile-check,gate,tag-modules,publish-bake}.yml`); MeshWeaver.Plugins / .Education / .Reinsurance / .SocialMedia call them and keep only repo-specific policy. Adopting one renames that repo's required-status-check contexts to `<caller job> / <name>` — do it in the same change. Full reference: [/ci](.claude/skills/ci/SKILL.md).

## GitHub PR Operations

🚨 **Finishing a change set means MERGED — merge it yourself on green, don't ask for permission.** A PR left open with a link handed back is unfinished work; the safety IS the gate (green CI plus the automatic Copilot review, which you never hand-request and never withdraw). Stop only when CI is red for a reason you cannot fix, when a review asks for a decision that changes what the change set IS, or when the work needs a scope call the user has not made. A change set spanning repos is finished when every part is merged in dependency order: platform first, then what depends on it.

🚨 **PR capability is CREDENTIAL × REPO — measure it for the repo you are in, never remember it** (`gh api repos/Systemorph/<repo> --jq '.permissions'`; `gh auth status`). The same repo answers differently to two sessions on the same day. **Never reach for `--admin`** — a refusal is information about the gate, not an obstacle to route around; on `FORBIDDEN`, re-authenticate with `! gh auth login`.

🚨 **Poll the `MeshWeaver Build and Test` check SUITE by name via GraphQL and merge only on `COMPLETED/SUCCESS`.** Never wait for *all* check suites (an installed App that posts no runs leaves its suite `queued` forever), and never poll with `gh run watch` / `gh pr checks --watch` (REST rate-limit 403s masquerade as CI-red). `Consolidate test results` is the required check and the only one to require.

🚨 **SUBSCRIBE to a PR — one persistent `Monitor` over EVERY event you would act on**, armed when you open it: suite green, suite *any other* terminal conclusion, a new unresolved review thread, `mergeStateStatus = DIRTY`, and `MERGED`/`CLOSED`. A success-only watch is the same defect as a gate that skips on missing input — silence looks identical to "still running".

🚨 **Merging is a shared action.** A merge supersedes the run QUEUED behind the one in flight, including one another session is waiting on. Before merging, check whether main has a run someone is gating a deploy on; if so, **hold and say so** — and push that hold to your subagents explicitly, because their default is merge-on-green.

Full reference: [/pullrequest](.claude/skills/pullrequest/SKILL.md) · delivery, batching and image verification: [/release](.claude/skills/release/SKILL.md) · workflow/gate authoring: [/ci](.claude/skills/ci/SKILL.md).

## 🚨 Postgres: One Schema Per Partition

**`public.mesh_nodes` is empty by design.** Data lives in per-partition schemas (`acme.mesh_nodes`, `rbuergi.mesh_nodes`, …), satellites routed by path segment (`_Access` → `access`, `_Thread` → `threads`, `_Activity` → `activities`, `_Comment`/`_Approval`/`_Tracking` → `annotations`, `Source`/`Test` → `code`). **`namespace` keeps the partition prefix — never strip it** (`rbuergi/ApiToken`, not `ApiToken`). **Never run raw `psql UPDATE` on a live portal** — it bypasses the workspace cache; use `MoveNodeRequest` or a Repair vN migration. 🚨 **Provisioning a partition schema is REACTIVE + POOLED: `EnsurePartitionProvisioned(namespace).SelectMany(_ => write…)`** — never declare a `PartitionDefinition` node to force a schema (it provisions the name verbatim while writes hit the lowercased one → 42P01), and never lowercase by hand.

Full reference: [/mesh-data](.claude/skills/mesh-data/SKILL.md) · [PostgresSchemaArchitecture.md](src/MeshWeaver.Documentation/Data/Architecture/PostgresSchemaArchitecture.md).

## 🛡️ Global admin = admin on the Admin partition

**"Global/platform admin" has ONE meaning: `Permission.All` at scope `Admin`** — an `AccessAssignment` granting the `Admin` role in the **`Admin/_Access`** namespace. This is a **platform admin, NOT a data superuser**: it does NOT grant access to spaces or user partitions, and emergency cross-partition data change requires explicit **elevation (break-glass)**, never standing access. A **root** `_Access` grant is the data-superuser shape and is deliberately NOT how platform admins are provisioned. **The one predicate is `hub.IsGlobalAdmin()` / `hub.IsGlobalAdmin(userId)`** — never an ad-hoc role-name or root-scope check — and **the grant lives in `Admin/_Access`, never root `_Access`**: a writer/reader split silently locks admins out of every admin tab.

Full reference: [AccessControl.md](src/MeshWeaver.Documentation/Data/Architecture/AccessControl.md) → "The Admin partition".

## Documentation

All docs are embedded in `src/MeshWeaver.Documentation/` and served under `Doc/` at runtime (`Data/Architecture/`, `Data/DataMesh/`, `Data/GUI/`, `Data/AI/`). **Agent and Skill node definitions ship with the AI engine, which lives in `MeshWeaver.Plugins` (#2276)** — not in this repo.

<!-- shared-rule:begin conserve-work-products -->
**🗂️ ALWAYS conserve work products — a design, an architecture decision, an investigation finding, a manual produced while working gets COMMITTED to this repo in the same change set, every time.** The durable form is <!--slot:doc-home-->a doc page under `src/MeshWeaver.Documentation/Data/` (Architecture for platform designs; follow AuthoringDocumentation.md; add a What's New entry when user-facing)<!--/slot--> — issue comments, PR bodies, chat replies and rendered artifact pages are *pointers* to the committed page, never a substitute for it. A finding that lives only in an issue thread or a terminal is invisible to the next session<!--slot:reach--> and to the portal; the doc tree ships with the platform<!--/slot-->. Maintainer directive, 2026-08-30<!-- shared-rule:end conserve-work-products -->. This rule holds in EVERY repo of the fleet — satellites commit theirs to their own doc home.

**Writing/editing a doc page:** follow [AuthoringDocumentation.md](src/MeshWeaver.Documentation/Data/Architecture/AuthoringDocumentation.md). Links resolve against the page's FULL node path at render time — sibling links need `../Sibling`, absolute links start `/Doc/…`; `xref:` and `.md` suffixes never resolve. `DocumentationLinkIntegrityTest` fails on any broken internal link — run it after doc edits (it travels with the Agent/Skill partitions it also validates, so it moves to MeshWeaver.Plugins with the AI engine).

**Node FILE formats (every `Data/` tree and every node repo):** the extension is a convention, not a free choice. **Agents and skills are `.md`** — front matter (`nodeType: Agent`/`Skill`) carries the configuration, the body IS the instructions; never JSON with an escaped instructions string. **C# source nodes are `.cs`** with the `// <meshweaver>` heading block (`// Id:`, `// DisplayName:`, optional `// NodeType:`); never JSON with the code in an escaped string. **`.json` remains** for typed nodes and executable code cells — the `.cs` header carries ONLY those three keys, so a cell with `isExecutable`/`activityParentPath` (or node-level `description`/`order`) authored as `.cs` silently loses its Run button on import.

**Hub-handler test hangs or a message disappears:** read [DebuggingMessageFlow.md](src/MeshWeaver.Documentation/Data/Architecture/DebuggingMessageFlow.md) first — never rerun a hung test "to see". **`type 'X' is not registered in this hub's TypeRegistry`:** the fix is `WithType(typeof(X), nameof(X))` on the receiving hub. **Use `hub.Observe(...)`, not `RegisterCallback`/`AwaitResponse`** — those overloads are `[Obsolete]` and deadlock.

🏗️ **THE UNIFIED BUILD PROCESS is [Module Build Architecture](src/MeshWeaver.Documentation/Data/Architecture/ModuleBuildArchitecture.md) (`get Doc/Architecture/ModuleBuildArchitecture`) — one shape, every repo:** the platform image is the compiler and the reference set, everything shared stages ONCE per run in the blob-backed actions cache, one Roslyn workspace builds the graph fail-fast, outputs are content-addressed (unchanged ⇒ no compile), gates compile against implementation frameworks, CI logs warn/error + verdicts only, and scripts are centralized (the lane fetches the platform's copy at the pin; repos keep only allow-files). Never hand-roll a repo's build; a deviating repo is behind, not different.

📘 **The full manual — what you author, what the build derives, the `src/` blind spot, and the closure boundary — is [Module Versioning](src/MeshWeaver.Documentation/Data/Architecture/ModuleVersioning.md) (`get Doc/Architecture/ModuleVersioning`). Read it before bumping anything.**

🚦 **Before trusting a green wall or debugging a red: [Reading CI Signals](src/MeshWeaver.Documentation/Data/Architecture/ReadingCiSignals.md) (`get Doc/Architecture/ReadingCiSignals`) — `SKIPPED` and *absent* required contexts count as SATISFIED, a red on a non-required check does not block, and the i18n mirror reds every Plugins PR until it lands. A required context counts only when it reads literally `=SUCCESS`.**

## 📬 Mail on the user's behalf: the assistant DRAFTS, the human sends

Anything that touches a user's mailbox goes through the **Executive Assistant** and that user's own
delegated credential — never a token an agent mints for itself. `Email:AgentSend` defaults to
**`DraftOnly`**, and in that mode the send tools are *never handed to the model at all*: the agent
has `DraftMail`/`DraftReply`, writes into the person's Drafts, and the person presses Send — so
never promise "I'll send it", say what the draft will contain. **No mail tool attaches a file** (a
message carrying one goes through **Share ⇒ as email** / `SendDocumentDispatch.ExportAndSend` with
`DocumentDelivery.Attachment`, which sends as the user off the same `EaCredential`). **A wrong draft
is AMENDED, never re-drafted** — `GetDraft` → `UpdateDraft` (a PATCH; the body REPLACES), or
`DiscardDraft`; two drafts hand the reviewer a choice they should not have to make. 🚨 **Both
writers re-read `isDraft` from Graph INSIDE the write** and refuse when it is no longer one: the
person may have pressed Send since the agent read it, and patching or deleting then would mutate
sent mail. A tool answering *"I don't have access to your mailbox and calendar yet"* is the just-in-time
consent step, not a missing capability: hand the user `{BaseUrl}/auth/ea/connect` and wait.
Full reference: [ExecutiveAssistant.md](src/MeshWeaver.Documentation/Data/AI/ExecutiveAssistant.md)
(`get Doc/AI/ExecutiveAssistant`). The plugin itself lives in MeshWeaver.Plugins.

## 🌍🌍🌍 ALWAYS think about internationalization — every user-visible string, every time

**Before you write ANY text a user will read, stop and ask: "how does this render for a German viewer?"** This is part of writing the feature, not a follow-up ticket. The portal ships English + German; **a hard-coded UI string is a bug** — buttons, labels, tooltips, `aria-label`s, placeholders, page titles, empty states, validation messages, toasts, dialog copy, menu entries, settings tabs, notification text and errors alike.

- **On a declaration** → `[Translation("de", "…")]` beside the existing `[Description]`. **Everywhere else** → a key in **both** `src/MeshWeaver.Messaging.Hub/Localization/strings.{en,de}.json`, read via `Access.Localize("key")` (Blazor) or `host.Localize("key")` (layout areas). `LocalizationTest` fails if a language is missing any English key.
- **Prefer text that needs no translation** — a language-neutral glyph (➕ ✏️ 🔖 ➡️ 📋 🗑️) plus a translated tooltip beats a translated label.
- 🚨 **A new key has a SECOND home in another repo**: `MeshWeaver.Plugins/clients/react/src/i18n/`. Deleted and relocated look identical from one repo — assume relocated until you have looked. Its drift guard DOES run (the plugins repo's `RN app + web clients (typecheck + test)` job) and it compares VALUES, not just keys — so it catches the divergence `LocalizationTest` cannot see. **Core is the source of truth: the core catalog change merges FIRST** and the mirror PR stays red until it does; never "fix" that red by reverting the mirror.
- 🚨 **Never resolve from `CultureInfo.CurrentUICulture`/`CurrentCulture`** — this covers date/number FORMATTING too. Resolution is always explicit off `AccessContext.Locale` (`ViewerLocale()`).
- 🚨 **Do NOT translate**: LLM tool-parameter `[Description]`s (model-facing), wire identifiers (`nodeType:Thread`, `RequestAction("New")`, Fluent icon names), or the glossary terms kept English on purpose (Thread, Mesh, Node, Agent, Skill, Harness, Provider, Namespace, Partition, Store).

Full reference: [/i18n](.claude/skills/i18n/SKILL.md) · [Localization.md](src/MeshWeaver.Documentation/Data/Architecture/Localization.md).

## Deployment

**Two deploy routes, different targets — neither deprecated. Pick by target, don't mix.** **AKS** (the shared cluster `memex` portal): a code update is *build image → set image → restart*; the cluster is PRIVATE, so `kubectl` runs ONLY through `az aks command invoke`; an env's `deploy.sh` is first-time ENV SETUP, never a code-update path, and env folders live in the private `Systemorph/Memex` repo. **Azure Container Apps** (the Aspire `test`/`prod` modes, via `tools/deploy.sh prod|test`): never point these at the AKS cluster.

🚨 **Before any AKS deploy, read [DeploymentAKS.md](src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md) end-to-end.** Nothing bakes at BUILD time on any machine — `BakeMeshLocalFeed` was removed (#395) and a legacy `#r "nuget:…"` hard-fails on a deployed image.

🚨 **The database migration is a run-once `Job` that `helm upgrade` runs — never a Deployment to roll.** The chart defines no such Deployment; a command aimed at one either errors or keeps a cluster-only orphan alive that re-runs the migration forever (#1788). A crash-looping migration pod is a FAILURE, not noise. Before declaring a deploy successful, confirm the Job logged `Database migration completed. Version: N` AND the portal serves HTTP 200; `DbVersionGate` stops the app with a `LogCritical` when the schema is behind, so a portal rolled ahead of its database refuses to serve rather than serving a half-migrated one.

🚨 **Verify the IMAGE, never the green tick** — read the running tag back off the deployment.

Full reference: [/deployment](.claude/skills/deployment/SKILL.md) · [Deployment.md](src/MeshWeaver.Documentation/Data/Architecture/Deployment.md) (index) · delivery/batching: [/release](.claude/skills/release/SKILL.md).

## Bash Command Guidelines

**Stay at the root of your worktree** — never the primary checkout, never a hard-coded path. Avoid chained commands (`&&`, `||`), `for` loops, and `cd` — they all require user confirmation. Avoid piping a build or test through `tail`/`head`: the pipeline's exit code becomes the pager's, hiding `Build FAILED`.

## Development Commands

```bash
dotnet build                                              # Solution (ONE project arg max — several is an MSB1008 no-op)
dotnet build test/MeshWeaver.Data.Test/MeshWeaver.Data.Test.csproj   # One project — required before --no-build
dotnet test test/MeshWeaver.Data.Test --no-build          # One test project (unbuilt = silent exit 0)
dotnet run   --project ../MeshWeaver.Plugins/src/Memex.Portal.Monolith   # Monolith  → https://localhost:7122
dotnet run   --project ../MeshWeaver.Plugins/src/Memex.AppHost           # Aspire (Docker) → https://localhost:7202
aspire run   --project ../MeshWeaver.Plugins/src/Memex.AppHost           # Same, via the CLI (registers with `aspire mcp`)
aspire start --no-build --project ../MeshWeaver.Plugins/src/Memex.AppHost  # Background, no rebuild; `aspire ps` / `aspire stop`
```

Changed code in `Memex.Portal.Distributed` or a project it references? **Don't kill the whole AppHost** — `dotnet watch` restarts only the affected resource; the dashboard's Resources → ⋯ → **Restart** is the fallback; a process kill is the last resort. Full reference: [LocalDevWorkflow.md](src/MeshWeaver.Documentation/Data/Architecture/LocalDevWorkflow.md).

## 🚨🚨🚨 ABSOLUTE: `GetMeshNodeStream().Update()` is the ONLY mutation API

**Every mesh-node mutation goes through `workspace.GetMeshNodeStream(path).Update(current => modified)`. There is no other mutation surface — do NOT invent one: no `SubmitMessageRequest`-style wire messages, no completion callbacks via `hub.Set<Action<...>>`, no bespoke `IRequest`/`IResponse` pairs for state changes. Migrate any straggler you touch to `stream.Update`.** The same API works for a node this hub does not own — the write routes to the owning per-node hub as an RFC 7396 merge patch, which the owner serialises, so concurrent writers never clobber each other's fields.

1. **Writes**: `stream.Update(current => current with { … })`. State-machine semantics? Set a `RequestedX` field and let the owning hub's watcher react.
2. **Reads**: `GetMeshNodeStream(path)` — server-side AND Blazor, via the process-wide `IMeshNodeStreamCache`. `GetRemoteStream<MeshNode, …>` is framework plumbing; never use it for a node by path.
3. **Delete the request type.** Writing `class XxxRequest` to mutate a thread / message / NodeType? Stop. Add a `RequestedXxx` field to the node's content and watch it from the owning hub.

**Observing completion**: subscribe to `GetMeshNodeStream(path)` and wait for the state on the node's `Content` — the GUI databinds the same way, and a test that posts a verb-shaped request and waits for a `*Response` is testing a deprecated API. **Thread and activity operations** have canonical `IMessageHub` extension surfaces (`hub.StartThread`/`SubmitMessage`/`ResubmitMessage`/`DeleteFromMessage`/`MarkThreadDone`/`RecordSubmissionFailure`, with the AI engine in `MeshWeaver.Plugins`; `hub.CancelActivity`/`RequestActivityStatus` in `src/MeshWeaver.Mesh.Contract`) — every one writes through `stream.Update`, and there is no other entry point. **Per-user work at logon is a `LogonAction`, never a SQL backfill** looping partition schemas; it runs as the USER and lands its effect plus ledger entry in ONE patch. **Sanctioned exceptions (NOT state mutations):** `CreateNodeRequest`/`DeleteNodeRequest`/`MoveNodeRequest` (lifecycle — they route, they don't mutate content) and transient queries that belong on no node.

Full reference: [/mesh-data](.claude/skills/mesh-data/SKILL.md) (which links `RequestViaStreamUpdate`, `MeshNodeStreamCache`, `ActivityControlPlane`, `ThreadOperations`, `LogonActions`).

## 🚨 Never write as hub — AccessContext propagation

**Every framework write primitive (`meshService.CreateNode/UpdateNode/DeleteNode/CopyNode`, `MeshNodeStreamHandle.Update`, `IMeshNodeStreamCache.Update`) automatically carries the caller's `AccessContext` through `.Subscribe()` boundaries** — keep writing the natural `.Subscribe(...)` shape. A write that must run as system/hub (legitimate infrastructure only — cache hydration, SyncStream heartbeats) says so explicitly: `using (accessService.ImpersonateAsSystem()) { … }` or `ImpersonateAsHub(hub)` / `o.ImpersonateAsHub(hub.Address)`. `PostPipeline` fails closed when no context is set, and the "silently stamp hub-self as principal" fallback was deleted because it masked a prod bug: application code that writes MUST have a real user identity on `AccessService.Context`. Full reference: [/async](.claude/skills/async/SKILL.md) · [AccessContextPropagation.md](src/MeshWeaver.Documentation/Data/Architecture/AccessContextPropagation.md).

## 🚨🚨🚨 ABSOLUTE: Nothing async, EVER — *NO* `async`, *NO* `await`, *NO* `Task<T>` in hub/UI code

**The user is LITERALLY NEVER OK with `async`/`await`/`Task<T>`/`Task.Run`/`.ToTask()`/`TaskCompletionSource`/`.Result`/`.Wait()` in any hub-reachable OR Blazor-view/component code.** It runs continuations on the wrong scheduler, deadlocks the single-threaded action block, and NotFound-storms a partition hub until the whole portal wedges. Everything is `IObservable<T>` end-to-end — compose with `.Select`/`.SelectMany`/`.Where`/`.Timeout` and **`.Subscribe(onNext, onError)`**, never `await`. Handlers, services and layout areas return `IObservable<T>` (or `void`), never `Task<T>`; click actions are `WithClickAction(ctx => { …; return Task.CompletedTask; })`, never `async ctx =>`. 🚨 **`.ToTask()` is FORBIDDEN EVERYWHERE, tests included** (maintainer, 2026-08-30): a Task completed inside an Rx pipeline resumes its awaiter inline on the signalling thread, still inside Rx's trampoline, and the continuation inherits that — so a "test-only" bridge changes what the test measures. Await the observable directly with a `.Timeout(...)`. The one place a bridge may work is inside an ACTIVITY, and even there avoid it.

🚨 **`Observable.FromAsync` is NEVER tolerated anywhere in `src/`** — no exceptions, no "storage is the hot path". It runs the prologue on the subscribing thread with no concurrency bound. **Every async / blocking / IO edge goes through `IIoPool`** (`pool.Invoke` / `InvokeBlocking` / `InvokeStream`, or `pool.Run*` for the promise-cached one-shot), resolved from the mesh-scoped `IoPoolRegistry`. Public surfaces return `IObservable<T>`, never `Task<T>`.

🚨 **Cold observables: Subscribe is mandatory.** Every write returns a cold `IObservable<T>` — the side effect runs on `Subscribe`, not on call, so a composed write you never subscribe to silently does nothing (the chat-doesn't-work root cause). `Update(...)` returns a `RequireSubscribeObservable` that logs a warning at GC if never subscribed — search the `MeshWeaver.Mesh.RequireSubscribe` log channel after every CI run.

```csharp
// ❌ fire-and-forget — cold, so the write never happens
workspace.GetMeshNodeStream().Update(node => node with { … });
// ✅ subscribe with explicit error propagation
workspace.GetMeshNodeStream().Update(node => node with { … })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "Update failed for {Path}", path));
```

Full reference: [/async](.claude/skills/async/SKILL.md) · [AsynchronousCalls.md](src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md) · [ControlledIoPooling.md](src/MeshWeaver.Documentation/Data/Architecture/ControlledIoPooling.md).

## 🚨🚨🚨 ABSOLUTE: Never cast an `object` payload — `.As<T>()` / `.ContentAs<T>()`, always

**`node.Content is MyType` / `payload as MyType` is a TRAP-DOOR.** It is correct only when the value already happens to be your CLR type, and yields a **silent null** in the three cases that actually happen in a running mesh: untyped JSON (the polymorphic converter degrades an unresolvable `$type` to a raw `JsonElement`), the as-written `JsonObject` DOM before the materialization pipeline re-types it, and a same-named type from another collectible assembly (every NodeType recompile mints one). All three look identical from outside — the value reads as absent, the view renders empty, a reactive wait never completes; no exception, no log, nothing to grep. Use `node.ContentAs<T>(hub.JsonSerializerOptions)` / `payload.As<T>(hub.JsonSerializerOptions, logger)` instead.

**🚨 FIRST, though: deserialize as close as possible to where the type IS registered** — a payload read on a hub that never registered the type is untyped by construction, and `.As<T>()` would be papering over a routing mistake. The durable fix is to move the work to the owning hub, or register the type where the read happens. Full reference: `src/MeshWeaver.Mesh.Contract/ObjectAsExtensions.cs` · [/mesh-data](.claude/skills/mesh-data/SKILL.md).

## 🚨 CQRS — Never Query for a Single Node's Content

`Query`/`ObserveQuery` are eventually consistent — **stale after writes**. Read a specific node from `workspace.GetMeshNodeStream(path)`, which is authoritative and live. **Valid query uses:** listing children, searching by predicate, autocomplete — anywhere a stale negative is harmless. **Wrong:** reading content by exact path, reading state before a write, polling for job completion, deciding create-or-skip for a known path. 🚨 **Existence of a SPECIFIC path is NOT a valid query use** — a query's negative can be minutes old, and where the target id is minted per attempt a stale negative produces DUPLICATE DATA (#2229).

🚨 **A node that may NOT EXIST YET needs BOTH halves — a `scope:children` listing for EXISTENCE, then `GetMeshNodeStream(target)` for CONTENT.** A point read of an absent node is a framework defect, not merely slow: the owner answers a routing NotFound that terminates the stream AND opens the storm-breaker on that path — and the breaker fast-fails WRITES too, so the read suppresses the write it is waiting for. The index trails the store, so "the index has seen it" implies "the store has it". Creating the node anyway? Skip the check and use `CreateOrUpdateNodeRequest`. This is about ONE known path you are GATING on — summing a SET for display off a `scope:children` query, `content` and all, is correct and cheaper than N point reads.

**Free-floating words → vector search.** A query with bare text tokens (`laptop nodeType:Story`) on a PG backend with an `IEmbeddingProvider` routes through the HNSW cosine index automatically; structured-only queries stay on the SQL path.

Full reference: [/mesh-data](.claude/skills/mesh-data/SKILL.md) · [CqrsAndContentAccess.md](src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md) · [VectorSearch.md](src/MeshWeaver.Documentation/Data/Architecture/VectorSearch.md).

## Mesh URL Shape · `@/` is Local-Only

`{baseUrl}/{meshpath}` — no `/node/` segment, no URL-encoding of separators. Prod `https://memex.meshweaver.cloud`; dev Aspire `https://localhost:7202` (fallback `http://localhost:5202`), Monolith `https://localhost:7122` (fallback `http://localhost:5022`).

`@/path` is a Unified Content Reference for markdown links (`[text](@/Path)`), autocomplete, and agent tool args — **never in `href=""` attributes or HTTP URLs**. Markdig strips `@` in native markdown syntax but NOT inside `<a href>`.

## 🚨🚨🚨 ABSOLUTE: No static collections — ever

**A `static` field that is a collection or cache is FORBIDDEN** anywhere in `src/` or `test/`: no `static ConcurrentDictionary`, `static Dictionary`, `static HashSet`, `static List`, `static ConcurrentBag`/`Queue`, `static MemoryCache`/`IMemoryCache`, `[ThreadStatic]`, or `static Lazy<…>` of mutable data. Process-wide static state survives mesh disposal, so it **bleeds across tests** — the moment you add a `Clear()` "for test isolation", that method *is* the proof of the bug — and across users/partitions in prod.

**Every cache and every repository is an instance owned by the mesh.** Register it in `MeshBuilder` (`ConfigureServices`/`WithServices`) as a **singleton** so its lifetime IS the mesh's; hold the backing store as an **instance field** on that singleton; resolve via `hub.ServiceProvider.GetRequiredService<T>()`. **Allowed `static readonly`:** immutable, read-only constant lookups initialized once and never written at runtime (media-type maps, reserved-word sets, role tables) — the instant something writes to one at runtime it must become a mesh-scoped instance singleton. Full reference: [NoStaticState.md](src/MeshWeaver.Documentation/Data/Architecture/NoStaticState.md).

## Collections Policy

**NEVER use mutable collections.** Always `System.Collections.Immutable`: `List<T>` → `ImmutableList<T>`, `Dictionary<K,V>` → `ImmutableDictionary<K,V>`, `HashSet<T>` → `ImmutableHashSet<T>`, `Queue<T>` → `ImmutableQueue<T>`. Exception: `ConcurrentDictionary` for concurrent mutation — **as an instance field on a mesh-scoped singleton, never `static`**.

## Architecture Overview

Actor-model message hub (`MeshWeaver.Messaging.Hub`) with address-based partitioning; UI is reactive Layout Areas. The AI engine (agents, threads, skills, the AI plugin surface) is a **module hosted in `MeshWeaver.Plugins`** (#2276), not part of this repo. Layout: `src/` core framework (50+ projects) · `samples/Graph/Data/` sample data nodes · `memex/aspire/` Aspire microservices · `../MeshWeaver.Plugins/src/Memex.Portal.Monolith/` the dev portal.

**Request-Response:** `hub.Observe<TResponse>(request, o => o.WithTarget(address)).Subscribe(resp => …, ex => …)`; the response is sent as `hub.Post(responseMessage, o => o.ResponseFor(request))`. **Fire-and-Forget:** `hub.Post(message, o => o.WithTarget(address))`. **Layout area route:** `@{address}/{areaName}/{areaId}`.

## Data Access Patterns

Never use `IMeshStorage` or `IMeshCatalog` directly — internal infrastructure only.

| Operation | API |
|---|---|
| Read (query) | `IMeshService.Query<T>(request)` — reactive. 🚨 There is **no** `QueryAsync` on the production interface; it survives only as a test-only bridge in `MeshWeaver.Fixture` |
| Read (single node) | `workspace.GetMeshNodeStream(path)` |
| Create/Delete | `meshService.CreateNode(node).Subscribe(...)` / `meshService.DeleteNode(path).Subscribe(...)` |
| Update | `workspace.GetMeshNodeStream(path).Update(current => current with { … })` |
| Move | `hub.Observe(new MoveNodeRequest(src, dst)).Subscribe(...)` |

Always `GetRequiredService<T>()` — never `GetService<T>()` + null check for required services. Full reference: [DataAccessPatterns.md](src/MeshWeaver.Documentation/Data/Architecture/DataAccessPatterns.md).

## Memex is available through MCP

The memex mesh is reachable through the **`meshweaver` MCP server** — wired automatically for agents working on this repo AND for the co-hosted Claude Code / GitHub Copilot harnesses, authenticated as the calling user. 🚨 An MCP server named after a *deployment* (e.g. a client portal) connects to THAT portal, not necessarily this user's memex — verify which mesh a tool talks to before any mutation. The mesh — NOT a local file tree — is the workspace: use the MCP tools rather than guessing (`get`/`search` to read; `create`/`update`/`patch`/`move`/`copy`/`delete` to mutate; `execute_script`, `render_area`, `navigate_to`, `upload`).

**For every MCP mutation, show a diff:** `get @path` before (cache the JSON) → mutate → `get @path` after → render a ` ```diff ` block of the changed region in your response. Read-only tools skip this: `get`, `search`, `recycle`, `get_diagnostics`, `navigate_to`, `execute_script`.

## Development Patterns

Detailed patterns with code examples: [UserInterface.md](src/MeshWeaver.Documentation/Data/Architecture/UserInterface.md) + [GUI docs](src/MeshWeaver.Documentation/Data/GUI/) (layout areas, controls) · [MessageBasedCommunication.md](src/MeshWeaver.Documentation/Data/Architecture/MessageBasedCommunication.md) · [AI docs](src/MeshWeaver.Documentation/Data/AI/) · [ActivityControlPlane.md](src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md) · [AsynchronousCalls.md](src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md).

**Static handlers for one-shot pipelines** — don't extract `IFooService` for DI cleanliness when there's no state; resolve deps via `hub.ServiceProvider.GetRequiredService<T>()` inside the static handler. **Operations with inputs + progress + output** (export, import, compile, mirror) → Code MeshNode template + form-bound inputs + `RequestedStatus = Running` trigger, not a bespoke `XxxRequest`/`XxxResponse` handler.

**Key dependencies:** .NET 10.0 · Orleans · Blazor Server · Microsoft.Extensions.AI · xUnit v3 · FluentAssertions · Markdig · Chart.js · Azure SDKs.

## Testing Guidelines

**No mocking.** Use `MonolithMeshTestBase` / `HubTestBase` and the real Orleans cluster machinery in `MeshWeaver.Hosting.Orleans.TestBase` — never mock `IMessageHub`, `IMeshService`, or core interfaces. **Always `run_in_background: true`** for test runs; **never `--verbosity minimal`** when tests may fail — it hides stack traces.

**Never `Task.Delay` to wait for propagation.** Wait on the actual condition via `stream.Where(...).FirstAsync().Timeout(...)`; for a request/response source wrap the re-query in `Observable.Interval(50.Milliseconds()).StartWith(0L).SelectMany(...).Where(predicate).FirstAsync().Timeout(...)`. Hand-rolled `while + Task.Delay(50)` poll loops are forbidden. Sanctioned uses: forcing distinct timestamps for sort assertions, and negative "nothing happened" tests with no positive signal to filter for. **Never assert "exactly N change events"** on a stream backed by pg_notify or any change feed that can race the initial snapshot — filter on the emission shape instead.

**🚨 NEVER re-run a test unless code under test has changed.** Re-running to "see if it was a flake" hides the bug — flakes are real races. Only exceptions: the harness itself crashed, or the user killed the previous run.

**When CI fails, do NOT run entire test projects** — read the failed test names from the run, build the one project, then iterate with `--filter "FullyQualifiedName~<TestName>" --no-build`. No skipping: CI-only failures catch real timing/state bugs. Build first, every time — `--no-build`/`--no-restore` against a project this worktree never built exits 0 having run nothing.

🚨 **xUnit's `methodTimeout` (30 s, `test/xunit.runner.json`) only bounds time spent INSIDE a test method** — a wedge in fixture construction, class init or teardown runs unbounded, so always give a local run its own wall-clock cap (background it and hold the deadline yourself; there is no `timeout` binary here). If a run hits a cap it is stuck — do not raise the bound.

Full reference: [/testing](.claude/skills/testing/SKILL.md) · [WritingTests.md](src/MeshWeaver.Documentation/Data/Architecture/WritingTests.md).

## Project Structure

Framework code in `src/`, tests in `test/`, samples in `samples/`. Main branch: `main`. Solution file: `MeshWeaver.slnx` (50+ projects). Package management: `Directory.Packages.props` — update this, not individual `.csproj` files.
