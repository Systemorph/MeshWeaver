---
name: ci
description: 'What CI proves, what it cannot prove, and how to write a gate that cannot pass on no evidence. Use when authoring or changing a GitHub Actions workflow, adding a required check, wiring a satellite repo CI, or reasoning about whether a green run actually covers your change. Covers the absolute no-skip-trapdoor rule (a gate must never carry continue-on-error on its input step nor an if that asks whether a secret is set — GitHub paints a skipped job the same colour as a passed one), the reusable workflow_call jobs satellite repos must call instead of hand-rolling, why runs on main are deliberately never cancelled, and the large body of in-mesh C# that no dotnet build or test ever type-checks.'
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /ci — a green tick is evidence only if the gate could have failed

## 🚨🚨🚨 ABSOLUTE: a gate NEVER tests its own inputs — no skip-trapdoors

**A CI gate must never carry `continue-on-error:` on the step that fetches its input, nor an `if:`
that asks whether a secret/variable is set** (`if: ${{ vars.X != '' }}`,
`if: steps.token.outputs.present == 'true'`, `if: steps.checkout.outcome == 'success'`). GitHub
renders a **skipped job with the same grey/green tick as a passed one**, so "the gate never ran" and
"the gate passed" become indistinguishable — and a required check that passes on no evidence is
worse than a flaky one.

This is not theoretical. The cross-repo plugin gate was built that shape and therefore **never ran
once**: its checkout failed (the secret was unprovisioned), `continue-on-error` rewrote the failure
to `success`, the compile step skipped, and the job reported green. #683 deleted
`AiSettingsNodeType.AddSkillSource` with a live caller in the plugins repo and put **nine** plugin
partitions on the compilation-error overlay in production; a separate `AddTracking` deletion broke
`SocialMedia/Post` the same way.

**The shape instead:**

- **One `preflight` job** asserts every CI input that comes from outside the tree (secrets, repo
  variables) and **fails RED naming exactly what to provision**. Adding an input = one line in its
  `missing` array.
- **Gates depend on it (`needs: [preflight, …]`) and run unconditionally** — no input-shaped `if:`.
- **The ONE legitimate exemption is a FORK PR** (GitHub withholds org secrets by design). Express it
  **once**, as a check on the *event* (`github.event.pull_request.head.repo.fork != true`) — never
  as a "the secret is empty" check. At the job level those two look identical and only one is safe.
- **Propagate into the required check.** `collect-results` runs with `always()` and is the ONLY
  required status check, so it needs `preflight` in `needs` **plus an explicit fail step** — a
  skipped dependency does not fail an `always()` job, which would re-open the trapdoor one level up.

**Legitimate `continue-on-error` (do not "fix" these):** the `Publish Test Results` reporter (the
TRX summarize step is the real gate; a GitHub-API 429 must not fail the run) and the green-marker
push/prune (losing a marker costs a redundant run, never correctness). The test is *what does a
failure here hide?* — nothing, for a reporter; everything, for a gate.

The same shape applies to a guard **test**: a guard that enumerates a directory which no longer
exists, or scans a file list whose subject has moved, passes having checked nothing. When content
moves, move its guard's roots in the same change.

## 🚨🚨🚨 ABSOLUTE: green CI does NOT mean the mesh compiles

**Every `.cs` stored in a mesh node — NodeType `Source/*.cs`, Scripts, layout areas — compiles at
RUNTIME in the portal, NEVER in CI.** The repo's node trees are `<None>` content
(`samples/Graph/MeshWeaver.Samples.Graph.csproj`), so thousands of lines of C# under
`samples/Graph/Data/` — and under every node repo's content tree — are never type-checked by any
build or any test. Worse, **a NodeType's `configuration` lambda is C# stored in a JSON string
field** — so it is invisible to every `.cs`-shaped habit at once: `grep --include='*.cs'`,
`dotnet build`, and any compile gate that only scans `Source/`. When you delete a framework symbol,
search the node **JSON** too.

**A framework-version bump recompiles EVERY dynamic NodeType** (`HasUsableBuild` rule 3), so
breakage never trickles in — the whole accumulated backlog detonates on one deploy. A NodeType left
at `CompileError` **refuses portal readiness** and parks every instance hub for the full **60 s**
activation budget: hung pages, failed liveness probes, dropped silos.

- **Deleting or renaming ANY public framework surface is a breaking change to code the compiler
  cannot see.** Extension methods on `MessageHubConfiguration` / `IMessageHub`, `Controls.*`,
  `host.*` helpers, content base types — before you delete one: `grep -rn "<Symbol>" samples/*/Data`
  **plus the node repos' content trees**, AND search the live mesh (`search_chunks`), which may hold
  callers the repo has already dropped. 🚨 **`"searched": false` in the answer is a sweep FAILURE,
  not a pass** (#2741) — the deployment has no embedding provider, so nothing was searched. This is
  the same shape as the `LspDiagnosticsForNode` trap below, and it bit for the same reason: the
  envelope used to answer `{"count":0,"results":[]}`, which is byte-identical to "I searched and
  found no callers", and `count` is the field everyone reads. It now carries **no `count` at all**
  when nothing was searched, so the absent field is the tell. Sweep on a deployment whose index is
  live, or stop — do not delete on an unrun sweep. Port or delete callers in the SAME change. A
  clean `-c Release -warnaserror` build proves nothing here.
- **Before prod, sweep every NodeType green.** `Search('nodeType:NodeType')` →
  `LspDiagnosticsForNode` per type → fix roots first (a red upstream makes every dependent
  `UpstreamFailed`) → re-sweep until all read `Ok`. 🚨 **`ok:false` with a `status` other than
  `Compiled` is a sweep FAILURE, not a pass** — `Absent` (renamed/mistyped/not on this replica),
  `NotCompilable` (wrong kind of node), `Unavailable` (the owning hub did not answer) each mean that
  entry was never checked. Until #1592 the tool answered `{"ok":true,"diagnostics":[]}` for all
  three, so a sweep over stale paths reported all-green having verified nothing. **Warnings count**:
  `stayed an untyped JsonElement`/unregistered-`$type` means a view **renders empty** and layout
  areas "cannot be found"; `CS0105`/`CS8632` noise is the camouflage that hides the one fatal
  diagnostic.

Mechanism:
[NodeTypeCompilation.md](../../../src/MeshWeaver.Documentation/Data/Architecture/NodeTypeCompilation.md).
Full protocol: the `/code` Skill node shipped by the AI engine (MeshWeaver.Plugins, `#2276`) →
"In-mesh source is NEVER compiled by CI" + "The pre-prod sweep".

## 🚨 Runs on `main` are never cancelled — load-bearing, not a tuning choice

`dotnet-test.yml` sets `cancel-in-progress` to
`github.event_name != 'workflow_dispatch' && github.ref != 'refs/heads/main'`. Superseding stays on
for PR branches (that is where #2316's ~28% of runner demand is saved, and a later push there tests
a strict successor of what was cancelled). It is OFF for main because cancelling there loses two
different things at once:

- **Nothing builds the combination that LANDED.** `strict: false` means each PR was tested against
  the main it branched from, so the merged tree is first compiled by main's own run. Not
  hypothetical: five merges inside fifteen seconds put `CS0246: MeshOperations` on main on
  2026-08-26 — two independently-green PRs, a semantic conflict neither could see (#2412).
- **Nothing publishes.** CD's delivery gate keys on `Consolidate test results` reaching `success`
  **for that SHA**. CD *does* still fire on a cancelled run (`main-cd.yml` subscribes with
  `types: [completed]`, and cancelled counts as completed) — it just finds no success to act on.
  Before the fix, main's five consecutive runs from 20:28–20:38 on 2026-08-26 were all `cancelled`,
  each by the next merge.

**So do NOT re-introduce cancellation on main to save runner minutes.** Batching *publication* is
still right and `CD_BATCH_WINDOW_MINUTES` still does it; what is not right is batching by destroying
the evidence.

🚨 **Know exactly what this bought, because it is less than it looks.** `cancel-in-progress: false`
protects the run that is ALREADY RUNNING. It does not protect runs QUEUED behind it: a concurrency
group holds one in-progress plus one pending, and each new push supersedes the pending one. Measured
2026-08-27 — five pushes to main inside **fourteen seconds** (`05:59:22`–`05:59:36`): the first ran
to completion, the other four were `cancelled` with `run_started_at == created_at`, i.e. they never
executed a step. So a burst still leaves intermediate commits uncompiled; what it achieves is that a
burst can no longer leave *nothing* completed, which is what silenced CD entirely. The full fix is a
merge queue (#2412), which tests the prospective combination before it lands; this repo is still
missing the `merge_group:` trigger that MeshWeaver.Plugins already has.

## 🚨 Every job is HARD-CUT at 45 minutes — `timeout-minutes` is mandatory, literal, ≤ 45

Maintainer, 2026-09-02: *"hard cut ci runs after 45min — we pay all this."* GitHub's default job
timeout is **360 minutes**. A job without an explicit cap that hangs therefore bills a runner for six
hours — and when a required check `needs:` it, nothing behind it can merge for six hours either.

What it cost, measured the morning the rule was written: the reusable module-pack lane's `pack` job
had no cap; `dotnet test MeshWeaver.Mcp.Test` wedged before its first test on EVERY MeshWeaver.Plugins
run; **19 runs sat `in_progress` at once** (11 of them on `main`), each holding a runner up to 360
minutes; the three required checks all `needs:` that job, so no Plugins PR could merge and `main`
never reached `publish-bake` — which starved every satellite of a sealed publication. One missing line.

The rule, enforced by `.github/scripts/check-workflow-timeouts.py` (self-tested, fails RED):

- every job in every workflow declares `timeout-minutes: <literal integer>` with `1 ≤ value ≤ 45`
  — a `${{ … }}` expression is refused, because a cap that can only be evaluated at run time
  cannot be proven by reading the file;
- a job that `uses:` a reusable workflow is exempt *in the caller* (GitHub ignores the caller's
  value there) — the cap lives on the jobs INSIDE the reusable workflow, gated in the repo that
  defines it (this one, for every `node-repo-*.yml` lane);
- core runs the guard on itself in `dotnet-test.yml` beside `check-workflow-shell.py`; every
  satellite gets it through `node-repo-validate.yml`, which fetches the platform's script at
  `platform-ref` and runs it against the caller's tree — pin `platform-ref` beside the `uses:` sha.

Measured headroom on 2026-09-02: the longest honest jobs are the Plugins portal-host shards
(25–30 min), `release-images` (36–41 min) and the Education install shards (~27 min). A job that
reaches 45 is **stuck, not slow** — find what is not completing; never raise the bound (AGENTS.md →
"No band-aids"). And a cap that fires destroys the hung test's transcript with it (a host-cap kill
leaves no `.trx`), so when you are chasing a hang, capture the stack (`dotnet-stack report -p`)
BEFORE the cap does.

## Satellite CI = thin callers of THIS repo's reusable workflows

**Never hand-roll (or copy-paste) a node repo's CI.** The shared jobs live here as `workflow_call`
workflows — `.github/workflows/node-repo-{validate,compile-check,gate,tag-modules,publish-bake}.yml`
— and MeshWeaver.Plugins / .Education / .Reinsurance / .SocialMedia call them, keeping only
repo-specific policy (digest pin, gating, `repository_dispatch` receiver, their own `scripts/`).
Adopting one renames that repo's required-status-check contexts to `<caller job> / <name>` — do it
in the same change.

Full contract:
[CiContentBake.md](../../../src/MeshWeaver.Documentation/Data/Architecture/CiContentBake.md) and
[ContinuousDeliveryContract.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ContinuousDeliveryContract.md)
(which also carries the GitHub OIDC subject-format rule: register BOTH subject formats per repo).

## 🚨 A change that spans two repos lands DELETING-HALF-LAST — declare the pair

**Core's CI does not build the plugin repos, so a cross-repo break can surface nowhere except CD,
after the fact.** #2678 deleted nine public view classes here while its module half was still open:
eight `MeshWeaver.AI.Test` cases failed, which failed the `MeshWeaver.AI` bundle, which failed BOTH
of MeshWeaver.Plugins' required compile gates — every open PR there went red and its `main` was red
for two hours, on a change none of them made. Four more shapes are on #2689.

A PR whose diff **removes a `public` top-level type from `src/`** — a departure, a move (a forwarder
keeps the type IDENTITY, not the consumer's `.csproj` references), or a whole assembly leaving —
must declare its counterpart in the **PR body**:

```text
Pairs-with: Systemorph/MeshWeaver.Plugins#904
Pairs-with: none — <reason, ≥12 chars, when nothing outside core referenced it>
```

`Cross-repo pair (public surface)` resolves it through the API and stays red while the counterpart
is open, draft, closed-unmerged, or **merged into anything but its repo's default branch**
(Plugins#904 merged into `feat/collaboration-module` — "merged" alone is not landed). It is a
`needs:` of `Consolidate test results`, so it can actually block.

- **It fires on MEMBERS too (#3103, the sixth shape).** Core #3137 deleted two `public static
  readonly` fields of a type that stayed; Plugins' `MeshWeaver.Auth.Test` failed `CS0117` and
  `Portal hosts (shard 0)` was red on every Plugins PR for three hours — *"nothing was tested"*.
  The detector now keys public members (methods, properties, fields, consts, events, indexers,
  operators, constructors, nested types, positional record parameters, enum and interface
  members) by NAME under their type; a rename is a removal. Removing one overload of several is
  below that granularity by design.
- **Ordinary PRs never meet it.** Measured 2026-09-02/03: `main~25 → main` removes ZERO public
  types and exactly TWO public members (both #3137); `main~100 → main` removes 116 types, all of
  them the Maps/Indexing carve-out.
- **`Pairs-with: none` resting on a live-mesh sweep must quote `searched: true`.** A reason that
  contains `searched: false` is refused (#2741: no embedding provider, nothing was searched —
  #3137's PR read exactly that as "no callers"), and a reason that mentions a sweep without the
  positive marker is refused too.
- **Core dispatches NOTHING to a plugin repository.** A dispatcher that asked MeshWeaver.Plugins to
  build against a core pull request (`dependent-suites.yml`, #3103, 2026-09-03) was withdrawn the same
  day by the maintainer: *"none of the top-level repos should have any dependency to anyone else"*.
  The break a removed member causes downstream surfaces in the plugin repo's own CI when its
  `platform-ref` moves — that is where it is fixed, by the plugin repo.
- **It reads, it never checks out.** A checkout puts plugin SOURCE into core's build; an API read
  puts only a FACT into a verdict. That is the line `PlatformNeverDependsOnPluginsGuard` draws, and
  its `ApiReadLedger` enumerates the two reads on that side of it.
- **The `none` escape is a declaration, not a skip** — printed into the log, and refused without a
  reason. Core cannot see a private repo's callers; what the gate removes is nobody being asked.

Full reference:
[CrossRepoPairGate.md](../../../src/MeshWeaver.Documentation/Data/Architecture/CrossRepoPairGate.md)
· [RepositoryDependencyDirection.md](../../../src/MeshWeaver.Documentation/Data/Architecture/RepositoryDependencyDirection.md) § C.

## Checklist

- [ ] Removing public surface — a TYPE or a MEMBER of a kept type? `Pairs-with:` is in the PR body
      and the counterpart is MERGED into its repo's default branch; a `none` that cites a sweep
      quotes `searched: true`.
- [ ] Every job I added or touched carries a literal `timeout-minutes` ≤ 45 (`python3 .github/scripts/check-workflow-timeouts.py --root .` is green).
- [ ] No `continue-on-error` on a gate's input step; no `if:` asking whether a secret/variable is
      set. Fork-PR exemption expressed once, on the event.
- [ ] Missing external inputs fail a `preflight` job RED, naming what to provision.
- [ ] The required check has `preflight` in `needs` **and** an explicit fail step.
- [ ] Deleting a public framework symbol? The node JSON and the live mesh were searched too.
- [ ] Not adding `cancel-in-progress` on `main`.
- [ ] A satellite repo's CI calls the reusable workflow rather than copying it.
