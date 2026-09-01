---
name: testing
description: 'Write and run MeshWeaver tests, and triage a red CI run. Use when adding a test, running one locally, or reading a CI failure. The house standards are non-negotiable: no mocking of IMessageHub / IMeshService / core interfaces (use the real mesh test bases), never Task.Delay to wait for propagation (wait on the condition), never assert an exact change-event count on a change feed, and never re-run a test to see whether it was a flake. Also covers the xUnit v3 configuration, why methodTimeout does not bound fixture or teardown wedges, and how to read a shard artifact (trx TextMessages, the trace log that survives a killed host, the CI exit markers).'
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /testing — real meshes, real conditions, one run

> Canonical references:
> [WritingTests.md](../../../src/MeshWeaver.Documentation/Data/Architecture/WritingTests.md) ·
> [TestStateIsolation.md](../../../src/MeshWeaver.Documentation/Data/Architecture/TestStateIsolation.md) ·
> [CqrsAndContentAccess.md](../../../src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md) ·
> [SatelliteEntityPatterns.md](../../../src/MeshWeaver.Documentation/Data/Architecture/SatelliteEntityPatterns.md).

## The standards

**No mocking.** Use the real mesh test bases — never mock `IMessageHub`, `IMeshService`, or core
interfaces (#1810).

**Always `run_in_background: true`** for test runs (they take minutes).

**Never `--verbosity minimal`** when tests may fail — it hides stack traces.

**Never `Task.Delay` to wait for propagation.** A fixed sleep races CI load: too short → flakes, too
long → wastes minutes across the suite. Wait on the actual condition via
`stream.Where(...).FirstAsync().Timeout(...)`. When the source is request/response (not an
observable), wrap the re-query in
`Observable.Interval(50.Milliseconds()).StartWith(0L).SelectMany(...).Where(predicate).FirstAsync().Timeout(...)`.
Hand-rolled `while + Task.Delay(50)` poll loops are forbidden. Sanctioned `Task.Delay` uses: forcing
distinct timestamps for sort assertions, and "wait to confirm nothing happened" negative tests where
there's no positive signal to filter for. See WritingTests.md → "Polling loops around QueryAsync".

**Never assert "exactly N change events"** on a stream backed by pg_notify or any change feed that
can race the initial-snapshot path. Filter on the emission shape (e.g.
`.Where(c => c.ChangeType == QueryChangeType.Initial)`), not the count.

**🚨 NEVER re-run a test (single or suite) unless code under test has changed.** Re-running to "see
if it was a flake" hides the bug — flakes are real races. Either fix the race or pin the failure
with a smaller repro; do not retry. The only exceptions: (a) the test harness itself crashed
(MSBuild MSB4166, infrastructure error — a re-run is the same input), (b) the previous run was
killed by the user before completion.

🚨 **`.ToTask()` is FORBIDDEN in tests too** (maintainer, 2026-08-30 — the earlier test exemption is retracted: a Task completed inside an Rx pipeline resumes inline on the signalling thread, still inside the trampoline, so the bridge changes what the test measures). Await the observable directly with a `.Timeout(...)` — see
[/async](../async/SKILL.md).

**No static collections, in `test/` as much as `src/`** — a `Clear()` added "for test isolation" is
the proof of the bug, not the fix.

## Configuration and its blind spot

xUnit v3 config (`test/xunit.runner.json`): `parallelizeAssembly: false`,
`parallelizeTestCollections: false`, `maxParallelThreads: 1`, `methodTimeout: 30000` (30 s).

🚨 **`methodTimeout` only bounds time spent INSIDE a test method.** A wedge in fixture construction,
class init, or teardown is outside it and runs unbounded — on 2026-08-04 an orphaned local run sat
at a pegged core for 25+ minutes and, together with a leaking e2e container, drove the colima VM to
128 MB free / load average 195, OOM-killing unrelated containers.

So **always give a local test run its own wall-clock cap** — but **not with `timeout`**: neither
`timeout` nor `gtimeout` exists on this macOS host, so `timeout 20m dotnet test …` runs **nothing**
and reports `command not found`. Background the run and hold the deadline yourself against `date -u`
— the five-step shape is in [/worktree](../worktree/SKILL.md) → "A verification step that cannot
fail".

CI has the equivalent at two levels — its runners are Linux, where `timeout` does exist: `timeout
8m` per project inside the shard loop, and `timeout-minutes: 20` on the shard job itself for a wedge
BETWEEN projects (which the per-project cap cannot see). If a run hits either, it is stuck — do not
raise the bound, find what is not completing (AGENTS.md → "No band-aids").

## Running tests

```bash
dotnet build test/MeshWeaver.Hosting.Monolith.Test/MeshWeaver.Hosting.Monolith.Test.csproj
dotnet test test/MeshWeaver.Hosting.Monolith.Test --no-build
dotnet test test/MeshWeaver.Graph.Test --filter "FullyQualifiedName~AccessAssignment" --no-build
```

**Build first, every time**: `dotnet test --no-restore`/`--no-build` against a project this worktree
has never built exits **0 with no output and no `.trx`** — an unmissable-looking pass that ran
nothing. One project per `dotnet build` invocation (several args is an `MSB1008` no-op).

Workflow: run once in background → read failures → fix → run once more.

## Test triage — when CI fails

**DO NOT run entire test projects.** Iterate one test at a time:

1. Read failed test names from CI logs (`gh run view <id> --log`).
2. `dotnet build <project>.csproj` — in a fresh worktree, skipping this makes step 3 a silent no-op
   (exit 0, no output, no `.trx`).
3. `dotnet test <project> --filter "FullyQualifiedName~<TestName>" --no-build`.
4. **No skipping** — CI-only failures catch real timing/state bugs.

🚨 **Read the run's evidence before reasoning about it.** The shard artifact carries three things and
they answer different questions:

- the per-project `.trx` — output lives in `<Output><TextMessages>`, **not** `<StdOut>`; the native
  xUnit v3 writer does not use `StdOut`, so finding it empty says nothing;
- `collected-logs/_meshweaver-test-trace.log` — the ONLY log that survives a host killed at the
  wall-clock cap, carrying `TEST_START`/`TEST_END` window markers and `[FAULT]` records with stacks,
  joinable by `pid=` and timestamp;
- the classified `[CI] <name> exit=<n>` markers.

A host that CRASHED is written into the trx as a `<project>.HOST_CRASHED` failure, so no summary can
report a pass over a dead process. Full map: WritingTests.md → "Reading a CI Failure".

A hub-handler test that hangs, or a message that disappears, is a message-flow problem, not a
timeout problem — see [/debug](../debug/SKILL.md) and
[DebuggingMessageFlow.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DebuggingMessageFlow.md)
first. Never rerun a hung test "to see".

## Test bases and fixtures

- **`MonolithMeshTestBase`** (recommended) — full integration with persistence, messaging, DI; use
  `AwaitResponseAsync(request, ...)` for request/response in tests.
- **`HubTestBase`** — message routing / layout tests; await the observable directly, e.g. `await hub.Observe<TResponse>(request).FirstAsync().Timeout(30.Seconds())` — never `.ToTask()`.
- **`MeshWeaver.Hosting.Orleans.TestBase`** — the core Orleans cluster machinery (test cluster,
  disposal drain, shutdown-race suppression). `OrleansMeshTestBase` is the ONE base a suite
  derives from; which cluster it gets is `Bootstrap => MeshBootstrap.Orleans(…)` plus
  `SiloConfiguratorType`. The AI-flavoured Orleans rig (the derived fixture and the swappable
  chat-client factory) ships with the AI engine in
  **MeshWeaver.Plugins**, not here (#2276).

**Use `hub.Observe(...)`, not `RegisterCallback`/`AwaitResponse`** — those overloads are
`[Obsolete]` and deadlock. Tests use `MonolithMeshTestBase.AwaitResponseAsync(...)`.

### DevLogin and access control

`MonolithMeshTestBase` auto-logs in `rbuergi@systemorph.com` as Admin. Available helpers:
`TestUsers.Admin`, `TestUsers.SampleUsers()`, `builder.AddSampleUsers()`.

For per-user access-control tests, use
`accessService.SetCircuitContext(new AccessContext { ObjectId = "...", Name = "..." })` before
creating test data; set `null` after.

### Node types available in a test mesh

From `AddGraph()`: `Markdown`, `Code`, `Group`, `User`, `VUser`, `Role`, `Notification`, `Approval`,
`AccessAssignment`, `GroupMembership`, `PartitionAccessPolicy`, `ActivityLog`, `UserActivity`,
`Comment`, `Redirect`.

`Agent`, `Skill`, `Thread` and `ThreadMessage` are declared by the **AI engine module**, which lives
in MeshWeaver.Plugins (#2276) — a core-only test mesh does not have them.

Custom types: `builder.AddMeshNodes(new MeshNode("MyType") { Name = "My Type" })` in
`ConfigureMesh`.

## Checklist

- [ ] Real test base, no mock of a core interface.
- [ ] Every wait is on a condition (`Where(...).FirstAsync().Timeout(...)`), never a sleep.
- [ ] No exact change-event count asserted on a change feed.
- [ ] The project was built before `--no-build`, and the run produced a fresh `.trx`.
- [ ] A failure was investigated, not re-run.
