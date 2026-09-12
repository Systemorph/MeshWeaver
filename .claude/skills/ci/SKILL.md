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
- **The ONE unconditionally legitimate exemption is a FORK PR** (GitHub withholds org secrets by
  design, and no maintainer action changes that). Express it **once**, as a check on the *event*
  (`github.event.pull_request.head.repo.fork != true`) — never as a "the secret is empty" check. At
  the job level those two look identical and only one is safe.
- **Propagate into the required check.** `collect-results` runs with `always()` and is the ONLY
  required status check, so it needs `preflight` in `needs` **plus an explicit fail step** — a
  skipped dependency does not fail an `always()` job, which would re-open the trapdoor one level up.

### 🚨 The SECOND secret store — and the one precondition for a `dependabot[bot]` exemption

**A run opened by Dependabot resolves `secrets.X` against a SEPARATE store** (Settings → Secrets and
variables → **Dependabot**), so a secret that is provisioned on the Actions tab and used by every
other run resolves EMPTY. The tell is `Secret source: Dependabot` in the run log, and the preflight
does exactly what it should: it fails RED naming a secret the maintainer can see in the UI. **The
remediation line must therefore name the STORE** — `Settings → Secrets → Actions` is misleading
here — and the fix is to provision the same names into the Dependabot store, never an `if:`.

Core's `dotnet-test.yml` *does* carry `github.actor != 'dependabot[bot]'` on `shared-rules` and
`cross-repo-pair`. That is legitimate for exactly one reason, and it is a **precondition, not a
precedent**: those gates also trigger on `merge_group`, where the Actions store IS available, and
the merge queue is the only path to `main` — *the exemption moves WHERE the gate runs, never
WHETHER it runs*. **Measured 2026-09-06: only `Systemorph/MeshWeaver` has a `merge_queue` rule.**
Plugins, Reinsurance, SocialMedia, Crm, Manufacturing, Education and Memex have none — several
carry a `merge_group:` trigger whose event never fires, which is the trap. Copying that `if:` into
a satellite is a skip-trapdoor, and a silent one: the satellites' required contexts are the gate
jobs themselves, and an absent required context counts as SATISFIED.

🚨 **Two traps that make this read as a workflow bug.** (1) **Only `secrets.` is doubled** — there
is no Dependabot *variables* store (`GET /repos/{o}/{r}/dependabot/variables` → 404), so
`vars.MW_TEST_IMAGE` resolves perfectly in the same run whose `secrets.ACR_USERNAME` is empty.
Check which namespace reads a name before concluding anything. (2) **The preflight's list is not
the denominator.** A secret can be CONSUMED by a pull-request job that no preflight ever asked
about; the empty value then surfaces deep in a later lane naming no secret at all. Measured on
MeshWeaver.Reinsurance#128: `Required CI inputs` **passed**, `compile-check` died one job later on
`compose-sealed-modules.sh: --registry-url needs --registry-key` — an absent `MW_REGISTRY_KEY`.
Scoring that run by the preflight list would have called it *no gap*.

**`check-pr-secret-preflight.py` enforces the half that can be enforced**: every `secrets.NAME` a
pull-request-reachable job consumes must be asserted by a preflight (`[ -n "${NAME:-}" ]`), so a
store gap is named in the first cheap job. It is STATIC — no credential, because none exists:
`GITHUB_TOKEN` has no `secrets`/`dependabot-secrets` permission key and neither org App holds one
(`meshweaver-cloud`: contents/metadata/pull_requests; `fleet-reader`: the same, read-only). Which App
mints which token: [GitHubAppCredentials.md](../../../src/MeshWeaver.Documentation/Data/Architecture/GitHubAppCredentials.md).
It runs in core's `dotnet-test.yml` and
reaches every satellite through `node-repo-validate.yml` at the caller's `platform-ref`. Exemptions
are one reasoned line in `.github/pr-secret-preflight-allow.txt` (a stale entry fails); `secrets:
inherit` is refused, because the guard cannot prove completeness through it. For the store diff
itself use `--check-stores --repo owner/name` with your own admin credential — names only, and
remember it cannot see a name present with an EMPTY value, which the in-run assertion can.

Full reference: [DependabotSecretStore.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DependabotSecretStore.md).

### 🚨 A SCHEDULED lane's honest red is red in an EMPTY ROOM — same defect, other end

The skip-trapdoor above is *"the gate could not fail"*. This is its twin: **the gate failed
correctly and nobody was told**, and from outside the two are indistinguishable, because both
produce silence.

Measured 2026-09-07 on `node-repo-platform-ref-bump.yml`. That lane had **every** property this page
demands — a minted App token, an assert that fails RED naming what to provision, no
`continue-on-error` anywhere, and a header arguing (correctly) that tolerating a failure there would
be worse than none. It had also **never once succeeded**: total runs in its entire history,
MeshWeaver.Plugins **1** (run 34083504064), MeshWeaver.SocialMedia **1**, both failed at the push
because the `meshweaver-cloud` installation holds `contents`/`metadata`/`pull_requests` and nothing
else (`GET /orgs/Systemorph/installations` — the *installation's* granted set is what an installation
token is minted from, and the authoritative read). A scheduled run hangs off no pull request, no
reviewer and no check list, so it went red daily into nothing.

What the silence cost, in one morning: the pin froze at 04:32Z, drifted 120 → 153, and at 08:23Z the
staleness ratchet reddened **all 16 open pull requests** in the repo at once on a gate none of their
diffs could reach; nothing sealed, so a corrected course quiz never reached its learners and the
session chasing *that* was three repositories away from the cause.

**So a lane that runs on `schedule:` (or `workflow_dispatch`, or any trigger with no pull request
attached) must route its failure onto an artefact that outlives the run** — here, one tracking issue
in the calling repo, labelled for that subject alone. Three properties, each with an
attractive-looking removal, each now guarded:

- **branch on the job's RESULT, not `if: failure()`** — a failure-only reporter never clears a stale
  alert, and an alert that is no longer true is how the next real one gets skimmed past;
- **write an issue, not a log line** — the log line goes into the same empty room the red went into;
- **no `continue-on-error` and no input-shaped `if:` on the reporter** — an alerting path that
  swallows its own failure re-creates the silence one level up.

Two mechanics worth knowing before you add one. A **label of its own**, never the repo's shared
`ci-failure`: listing by a shared label finds whatever unrelated alert is open, comments this
subject's story onto it, and then *closes* it on the next success. And in a reusable workflow the
reporter's `issues: write` must be granted by the **caller** — a called workflow can only narrow what
it was given, and omitting it does not degrade quietly: the run ends in **`startup_failure` with zero
jobs created** (measured, run 34137399515). Loud, which is right, but it means the caller's grant and
its `uses:` sha move in one commit.

🚨 **The tempting alternative — "page earlier" — is the wrong instrument.** Lowering the staleness
bound so it warns before it breaches fires on every open pull request for a condition none of their
diffs caused: the breach's own harm, more often. A gate that fires constantly gets bypassed. The
approach signal already existed and *was* the daily lane; what was missing is an **observer of the
mover**. Note also that the breach red teaches the wrong lesson while the mover is dead — *"the pin
is stale, move it"* → hand bump → mover still dead → recurrence.

Full reference:
[PlatformRefBumpLane.md](../../../src/MeshWeaver.Documentation/Data/Architecture/PlatformRefBumpLane.md).

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
(25–30 min), the retired `release-images` rebuild lane (36–41 min) and the Education install shards (~27 min). A job that
reaches 45 is **stuck, not slow** — find what is not completing; never raise the bound (AGENTS.md →
"No band-aids"). And a cap that fires destroys the hung test's transcript with it (a host-cap kill
leaves no `.trx`), so when you are chasing a hang, capture the stack (`dotnet-stack report -p`)
BEFORE the cap does.

## Satellite CI = thin callers of THIS repo's reusable workflows

**Never hand-roll (or copy-paste) a node repo's CI.** The shared jobs live here as `workflow_call`
workflows — `.github/workflows/node-repo-{validate,compile-check,gate,tag-modules,publish-bake}.yml`
— and MeshWeaver.Plugins / .Education / .Reinsurance / .SocialMedia / .Crm / .Manufacturing call
them, keeping only repo-specific policy (digest pin, gating, `repository_dispatch` receiver, their
own `scripts/`). Adopting one renames that repo's required-status-check contexts to
`<caller job> / <name>` — do it in the same change.

> 📅 **2026-09-12 — two of those "repo-specific" items no longer exist, by decision.** There is no
> digest pin (the platform is resolved at run time to the newest SEALED set, #3842), and there is no
> `meshweaver-framework-released` receiver: the per-build release wave is off by default
> (`Hosting:PlatformBuilds:BroadcastFrameworkReleases`, Plugins#1707) and Plugins#1709 /
> Reinsurance#198 / Crm#91 / Education#320 / SocialMedia#178 / Manufacturing#79 dropped the type. A
> satellite runs the shape above on its own pushes and once a day (`schedule`, staggered 03:03 →
> 03:56 UTC); the daily run is the full run and is what validates a platform build. Only
> `meshweaver-upstream-published` (a satellite's own publication reaching its declared dependents)
> still wakes a repo. The shape is unchanged and still mandatory; what changed is WHEN it runs. Full
> reference: `Hosting/BuildAndReleaseProcess` (MeshWeaver.Plugins; `get Hosting/BuildAndReleaseProcess` on the memex MCP).

🚨 **`node-repo-validate` is not one lane among several — it is where the FLEET-WIDE guards run**,
so a copy of it opts out of every guard the lane grows LATER: silently, retroactively, and
invisibly from inside the repo that made the copy. `check-workflow-timeouts.py` and
`check-pr-secret-preflight.py` both live inside it and neither existed when the older copies were
made — nobody chose to skip them, the copy chose, months of commits later. #3504 found Plugins (the
largest satellite, most lanes, most secrets) running **no** secret preflight at all, and its first
execution named three violations.

🚨 **Calling the lane is NECESSARY and NOT SUFFICIENT — the PIN must carry the guard**, and this is
the half an adoption table cannot see. Measured over every satellite's `main`, 2026-09-07: three
pre-existing callers (Crm, SocialMedia, Education) pin a `node-repo-validate` sha that predates
`check-pr-secret-preflight.py`, so they call the lane and are not checked by it — **4 violations
each, `MW_REGISTRY_KEY` among them**, which is precisely Reinsurance#128's absent secret. The
control that makes that a measurement rather than an assertion: Reinsurance, the ONE pre-existing
caller whose pin carries the guard, is the ONE at zero. Read a caller's staleness line ("Shared CI
logic pinned to `<sha>` — cut N days ago") as a coverage report, not a tidiness one, and bump every
`uses:` with its paired `platform-ref` in one commit. Full table:
[CiContentBake.md](../../../src/MeshWeaver.Documentation/Data/Architecture/CiContentBake.md) →
"Node repos run the same lane".

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

### 🚨 ADDING an interface member is the OTHER half, and its gate is the mirror image (#3465)

**A forwarder rescues a CALLER. It cannot rescue an IMPLEMENTER.** Core #3446 added three members to
`IEaGraphAuth` *with* default-implemented forwarders for the retiring `…Async` surface — so every
caller kept compiling, `Cross-repo pair` was correctly silent (nothing was removed) and core merged
clean. The satellite's pin move then hit `CS0535: 'FakeEaGraphAuth' does not implement interface
member 'IEaGraphAuth.ExchangeAndStore(…)'`, ×3, and the two halves DEADLOCKED on the release
critical path: the pin move could not compile without the adaptation, and the adaptation could not
compile without the pin. That repo's `main` was dark for hours. #3446's forwarder strategy was
**correct** — the gap is that callers and implementers have different compatibility rules.

`Interface additions (implementers declared)` fires when the diff adds a member an outside
implementer would have to write: a member on a **public interface** that already existed at the merge
base, with **no body** (no `=>`, no block, an accessor list of bare `get;`/`set;`/`init;` only) and
not `static` unless also `abstract`; or an **`abstract`** member on a public **abstract** class.

```text
Implementers: IFoo.Bar — <who implements it, where their update lands>
```

- **A DEFAULT IMPLEMENTATION silences it, and that is the intended fix** — it is the one change on
  this side that keeps every implementer compiling, so the gate must never tax it.
- 🚨 **The ordering is INVERTED from `Pairs-with:`.** The core half lands **first** — the dependent
  cannot compile its adaptation until this is pinned — so the gate wants the coupling PREDICTED, not
  a merged counterpart. Demanding one would recreate the deadlock. There is no blanket form: name
  each member, and a reason citing a live-mesh sweep must quote `searched: true`.
- **Core-only: no checkout, no API read, no credential, no ledger entry** — so unlike
  `Cross-repo pair` it also runs on FORK pull requests.
- **Ordinary PRs never meet it.** Measured 2026-09-06: over the last 40 first-parent merges, 15
  change the public declaration set and **one** meets this gate — #3446 itself. `main~100 → main`
  adds 91 public types and members, of which **3** oblige an implementer: the same three.
- **Two extra control arms**, because `publicTypesAtBase` does not constrain this shape at all:
  `publicInterfacesAtBase` and `implementerObligationsAtBase` (131 and 450 on `main`), with floors
  the gate refuses to pass below. Cross-checking the first against `grep` is what found that a
  **UTF-8 BOM** hid 16 public types from the removal gate outright and miskeyed 128 more.

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
- [ ] A lane on `schedule:`/`workflow_dispatch` routes its failure to a durable artefact (an
      issue on its own label, cleared on success) — an honest red nobody reads is the same defect as
      a gate that cannot fail.
- [ ] Adding a `secrets.X` to a pull-request job? A preflight in the same repo asserts it
      (`check-pr-secret-preflight.py` reds otherwise), and `vars.` vs `secrets.` is the right
      namespace — only `secrets.` has a second store.
- [ ] Adding a required secret? It is provisioned in **both** stores (Actions *and* Dependabot), and
      the preflight's remediation line names the store. A `dependabot[bot]` exemption only where the
      same gate runs on `merge_group` — core only.
- [ ] The required check has `preflight` in `needs` **and** an explicit fail step.
- [ ] Deleting a public framework symbol? The node JSON and the live mesh were searched too.
- [ ] Not adding `cancel-in-progress` on `main`.
- [ ] A satellite repo's CI calls the reusable workflow rather than copying it.
