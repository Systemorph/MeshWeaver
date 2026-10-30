---
Name: Combo Gate Wiring
Category: Architecture
Description: How the self-update decision consults the combo verification — the three verdicts, what an instance does on each (a Red is an advisory on the roll, never a hold), and why "could not find out" is neither a pass nor a refusal.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="m9 12 2 2 4-4"/></svg>
---

# Combo Gate Wiring

> 🚨 **The platform rolls independently of its packages** — policy
> `platform-rolls-independently-of-packages` ([Policy Not Prose](../PolicyNotProse)). A combo verdict
> is a statement about **packages**, so no verdict holds the platform roll: a **Red** is an
> **advisory** that names the failing modules, and the roll proceeds.

An image being **newer** says nothing about whether it can **serve** what an instance already runs.
A framework-identity change invalidates the whole assembly cache by design, and an optional
parameter added to a record's primary constructor *replaces* the signature — so a portal can roll to
a build that passed every CI job, has a sealed content bake, links every landed module, and still
aborts at boot with a `MissingMethodException` because a landed module binds a method that no longer
exists.

That is not hypothetical. The control instance was trapped between two failing states: rolling the
image forward gave it a new platform with its old landed modules, and re-fetching its bundles gave
it new modules with its old platform. Both aborted the host. It was only *up* because both halves
happened to be consistently old.

[Candidate Release Protocol](/Doc/Architecture/CandidateReleaseProtocol) already answered that
question — `InstanceComboVerifier` materialises every module of an instance's combo at its recorded
ref, runs `mw-plugin-test` inside the candidate image, and folds the evidence into one verdict. This
page is about the other half: **who asks, and what an instance does with the answer.**

## The shape

```text
 registry tags ──► VersionSelect.PickTargets ──► candidates, newest first
                                                     │
                                                     ▼
                                   the NEWEST admitted candidate — always
                                   (no package answer walks past a release)
                                                     │
                       ReleaseAvailabilityService.IsUpdatable
                                                     │
                          a package refusal ──► ADVISORIES on the roll, never HELD
                                                     │
                                                     ▼
                        ComboVerificationGate.Clearance(policy, tag, imageRef)
                                                     │
        ┌────────────────────────┬───────────────────┴──────────────────┬────────────────────────┐
        ▼                        ▼                                      ▼                        ▼
     Cleared                  Refused                              Unverifiable             NotVerified
     (Green)                   (Red)                             (NotVerifiable)         (no verdict at all)
        │                        │                                      │                        │
      roll             roll — ADVISORY, modules named             roll, recorded as UNVERIFIED
```

Every branch ends in a roll. What differs is what the instance **says** about it — on the log, on
`Admin/UpdatePolicy`, and on the durable check verdict.

The two gates answer **different questions** and are deliberately independent: the availability gate
asks *"does a usable artifact exist for the target?"*, the combo gate asks *"can the target serve
what this instance has already landed?"*. Both are questions about **packages**, and under
policy `platform-rolls-independently-of-packages` neither answer decides which platform build an
instance runs: the platform rolls, and a package that does not follow keeps its previous generation
and catches up through its own update policy. The deadlock that rule removes is recorded in
[Release & Self-Update Strategy](../ReleaseStrategy).

## Produce where possible, consult everywhere

Producing a verdict needs three things a portal pod does not have — docker, a writable
materialisation root, and read access to the module repositories. That is why `mw-combo-verify` is a
console tool in `tools/` and the protocol runs it off-cluster.

So `ComboVerificationGate` has two modes, and they are the same code path:

- **Consult** (every portal pod). The verdicts live on `Admin/UpdatePolicy` →
  `UpdatePolicyContent.ComboVerifications`, which the poller has *already read* to get the policy.
  Reading the verdict therefore costs **no additional mesh touch**, which is what keeps the poller's
  wedges-to-zero invariant intact: a degraded mesh cannot stop a roll-forward through this gate,
  because this gate performs no read of its own.
- **Produce** (a host that registers an `IComboGateRunner`). The gate runs `InstanceComboReader` →
  `InstanceComboAssembler` → `InstanceComboVerifier`, records the verdict through
  `UpdatePolicyNodeType.RecordVerification`, and decides on it. The record is **bookkeeping, never a
  gate**: a failed write is a warning naming the tag and the node, and the verdict still decides.

`IComboGateRunner` carries everything the pod lacks — the docker run, the repo fetch, and the
assembly policy — as one optional service, so the gate is identical whether it produced the verdict
or read one somebody else landed.

## Who actually produces one: `combo-verify.yml`

For most of this page's life the answer was **nobody**, and that made every sentence above
describe a mechanism that never ran. Measured 2026-09-07 (issue #3544):

- zero references to `mw-combo-verify` anywhere under `.github/` in this repo or MeshWeaver.Plugins;
- `RecordVerification` called from exactly one place, the in-process producer path;
- no production `IComboGateRunner` registered anywhere — the only implementation in either repo was
  a test fake, so `ResolveRunner()` returned null on every portal;
- `Admin/UpdatePolicy.comboVerifications` was `[]` on the public instance, and both portals said so in their
  own words: *"no combo-gate runner is registered on this host, so it produces no verdicts of its
  own — they are landed here by mw-combo-verify."*

So every self-update rolled `UNVERIFIED`, said so in the log, and **applied the update anyway**. The
gate built to prevent a `MissingMethodException`-at-boot roll was never once consulted with data.

`.github/workflows/combo-verify.yml` is the producer. It runs off the CD workflow's completion, and
per instance it does exactly the three steps
[Candidate Release Protocol](/Doc/Architecture/CandidateReleaseProtocol) describes:

1. **Ask the instance what it would roll to** — `GET /api/plugins/roll-target`. The candidate is
   asked OF the instance rather than derived in CI, because "the newest tag" is not the question the
   gate answers: the route returns the poller's own selection (the registry's portal tags under the
   instance's update policy, walked by `ReleaseAvailabilityService`), so it names the release this
   environment would actually take, and that release has a portal image. Re-deriving it would be a
   second rule. An `indeterminate` answer is red, never "nothing to verify". An instance on an
   image from before #4413 still selects over the `_releases` markers and can name a promoted but
   unarmed set; the pull then fails naming the absent `memex-portal-ai:<version>` until that
   instance rolls.

   **The selected version is a tag in the repository the instance rolls**, and the answer names
   that repository (`repository`). That is not always `memex-portal-ai`. The control instance rolls
   `memex-control`, whose version tag exists before `memex-portal-ai`'s is armed, and also exists
   for builds that never arm it. The lander used to pull `memex-portal-ai:<selected>` for every
   instance, so the control slot failed `not found` whenever its candidate was not armed yet (#4413).
   It now pulls `<acr>/<repository>:<selected>`. An instance on an image from before the field
   names no repository, and the lander falls back to the poller's default (`memex-portal-ai`),
   which is what that image lists.
2. **Ask the instance what it runs** — `GET /api/plugins/combo`, added by #3544 beside its two
   `mwi_`-gated siblings. Its body is an `InstanceCombo` serialized with
   `InstanceComboAssembler.Json`, i.e. it **is** `combo.json`, byte-for-byte what `mw-combo-verify`
   deserializes. Before it existed, `InstanceComboReader` had no HTTP, MCP or layout surface at all
   and the producer's first input was simply unobtainable from a running portal.
3. **Verify, then LAND the verdict** — `mw-combo-verify combo.json <acr>/<repository>:<candidate>`,
   then a read-merge-write onto `Admin/UpdatePolicy` through `/api/mesh/get` + `/api/mesh/patch`.

🚨 **The verdict is landed BEFORE the job's exit code is decided.** A Red that never reaches the
instance is worse than no verdict at all — the instance's own gate would then clear a candidate that
run had already proved it cannot serve.

🚨 **An RFC 7396 merge patch replaces an array wholesale**, so the whole list is sent and the merge
rule is not reinvented in CI: upsert by `candidateTag` (case-insensitive), newest first, capped at
`MaxRecordedVerifications` = 8 — the rule `UpdatePolicyNodeType.RecordVerification` applies
in-process.

🚨 **Green is the only pass.** A `Red` fails the job (delivery promoted a candidate that some of a
live instance's modules do not survive) and a `NotVerifiable` fails it too — that outcome means *nothing was checked*, which is
"the gate never ran" wearing the colour of "the gate passed".

### Why it is its own workflow, and why an instance's verify job is red until that instance is provisioned

The lane holds **no credential for any instance** (#3848). Each verify job reaches its instance as
*the run itself*: a GitHub Actions OIDC token minted per call, audience = the instance's base URL,
which the instance verifies against GitHub's JWKS and resolves to its
`Admin/_BuildPrincipal/systemorph--meshweaver` node (grant `verify:combo`). What each instance must
hold for that is in *Provisioning an instance* below. Until it holds both
halves, that instance's verify job is red with a 401 that names them.

A gate must never test its own inputs, so none of that is expressed as `continue-on-error` or
`if: secrets.X != ''`. A `preflight` job asserts every input and fails **RED naming what to
provision**; `verify` `needs:` it and carries no input condition at all. Because a `needs:` failure
*skips* `verify`, and GitHub paints a skipped job the same colour as a passed one, a third job —
`verdict` — runs `if: always()` and fails explicitly on anything that is not a success, and prints
the instance count so a zero cannot read as a pass.

It lives in its own workflow rather than inside `main-cd.yml` for one reason: that red is correct
and will persist until an operator provisions the credentials, and a red inside the workflow that
publishes the fleet's images would be read as "delivery failed".

`check-combo-verify.py` runs on every platform PR — because `combo-verify.yml` itself never fires on
one — and opens both halves of the lane's shell. It executes the preflight's real `run:` text over
five scenarios, including the empty-instance-list case whose empty matrix would otherwise skip
`verify` into a green; and it executes the lander's real verdict-merge `jq` over both node shapes
`/api/mesh/get` can return. That second half guards a data-loss path rather than a wrong answer: the
merge re-sends the WHOLE list, so reading the `{node, compilationError}` wrapper as if it were the
bare node yields null, null merges as an empty list, and the landing would replace up to eight
recorded verdicts with one. Both halves carry a `--self-test` that substitutes the defect and
requires the guard to catch it.

## The three verdicts, and the fourth state

`ComboVerification` is explicit that Green, Red and NotVerifiable are never conflated. The roll-side
fold lives in one pure function, `ComboClearance.For`, and its states map one-to-one:

| Recorded verdict | Clearance | What the instance does |
|---|---|---|
| `Green` | `Cleared` | Roll. The verdict's **caveats ride along** — a Green over a moving pin is not an unqualified pass. |
| `Red` | `Refused` | **Roll, with an ADVISORY.** Every failing module is named among the advisories on `Admin/UpdatePolicy`, the roll is logged at Warning, and the check verdict reads `… PACKAGE ADVISORY … the combo gate recorded RED — <why>`. The modules keep their previous generation and catch up through their own update policy. |
| `NotVerifiable` | `Unverifiable` | **Roll, UNVERIFIED.** No clearance, no advisory about a module. |
| *(none)* | `NotVerified` | **Roll, UNVERIFIED**, with a different sentence: nothing has asked. |

`Refused` is the clearance state's name — *the gate refused to clear the candidate* — and it is all
the name means. Under policy `platform-rolls-independently-of-packages` it does not refuse the **roll**:
`ComboThenApply` turns it into advisories (`WithComboAdvisory`) and applies.

### 🚨 Only a Green clears

This is the property that makes the gate a gate. There is deliberately **no configuration key, no
missing-service branch and no `catch`** that can produce `Cleared`. An unregistered gate, an
unregistered runner, a producer that faulted, a producer that timed out, and an unknown future
`ComboVerdictKind` member all land on a state that grants nothing — and the switch in
`ComboClearance.For` names Green and Red *explicitly*, with everything else falling through, so a
member appended tomorrow cannot silently become a pass.

No key turns a Red into a Green either. `SelfUpdateOptions.AllowUnverifiedRoll` no longer has any
effect — an availability gate that could not run is an advisory with or without it — and it never
touched a combo verdict: the Red stays a Red on the record and on every surface, whatever is
configured. What changed is what a Red **does**, not what it **is**.

### 🚨 Why `NotVerifiable` is neither

Both of the obvious answers are wrong, and each is wrong in a way this codebase has already paid for:

- **Treating it as Green** reproduces the outage. "We could not find out" reading as "all clear" is
  the false-confidence failure the whole protocol was written to prevent.
- **Treating it as Red** bricks self-update. Producing a verdict requires a host nothing in the fleet
  runs yet, so the very first evidence gap would freeze every instance — the fail-closed rule drawn
  one state too wide, whose cost is already recorded in `ReleaseGateApplicabilityTest` (holding a
  deployment the gate was never going to protect, and making a first-ever roll impossible).

So it does **neither**, and the answer is made *observable* instead of silent:

- it never clears — a `NotVerifiable` candidate is never reported as verified;
- it never names a module — nothing was found, so there is no advisory to record, and the roll
  proceeds as it did before this gate existed;
- the check verdict is **qualified** — `UpdatePolicyContent.LastCheckVerdict` reads
  `applied update X … UNVERIFIED — <why>`, durably, on the node the Updates tab renders;
- and a patch actually issued without clearance logs at **Warning**, naming the reason.

The durable half is not optional. A log line depends on a per-category log level a deployment may
never have set — which is exactly how an install sat three builds behind for seven hours with
nothing in the product able to say so (#2553). A node write does not.

`NotVerifiable` and *no verdict at all* behave identically and read differently, on purpose: "the
gate ran and could not answer" and "nothing has ever asked" are different incidents with different
fixes, and an operator has to be able to tell them apart from the recorded sentence alone.

## Where a Red shows up

The platform rolls over a `Red`, so the one thing a Red must never be is invisible: an operator has
to be able to read which modules did not follow the roll without a pod log. It lands in four places
at once:

1. **`ComboVerifications`** — the full verdict, per candidate tag. The **Updates** settings tab
   keeps the release line and renders the verdict underneath as an advisory (`ui.updateBlocked`:
   *"the platform still rolls to X, but these modules do not compile or test against it"*), listing
   every failing module and every caveat.
2. **`AdvisoriesTag` / `Advisories`** — the poller records the Red among the roll's advisories,
   leading: *"the platform rolls independently of packages — the combo gate recorded RED for this
   release: …"*. The hold fields (`HeldTag` / `HeldReason` / `HeldIndeterminate` / `HeldAt`) are
   **cleared**, not written — nothing is held. A hold an older build recorded is cleared by the
   same write, and on a check that finds nothing newer by the poller's no-candidate exit.
3. **`LastCheckVerdict`** — the durable sentence: `applied update X … PACKAGE ADVISORY (the platform
   rolls independently of packages; …): the combo gate recorded RED — <why>`.
4. **The About tab and the header build chip** — `PlatformUpdateStatus.Derive` reads a Red as
   *update available*, never as `UpdateHeld`. The About tab announces that package advisories exist
   in one translated sentence and points to the Updates tab; the module names and reasons are
   diagnostics and stay on that admin-scoped surface.

The same rule holds on the two paths a person starts. The **Apply update now** button does not
refuse on a recorded Red: it applies, logs at Warning, and lists the advisories on its result line.
The **reboot's image step** chooses the newest admitted tag and names the recorded Red in its detail
— also on a host with no availability gate registered — except for a wedged instance, which does not
wait for the combo verdict and says so.

Before policy `platform-rolls-independently-of-packages` a Red was a **hold**: it wrote
`HeldTag`, rendered as `UpdateHeld`, and the candidate walk stepped past the release.
`SelfUpdateOutcome.ComboBlocked` is the outcome that hold produced; the type is kept and nothing
produces it any more.

A `NotVerifiable` names no module — nothing was diagnosed — so it is recorded as `UNVERIFIED` on the
check verdict and never as an advisory about a package.

## 🚨 The verdict is read at DECISION time, never once at start-up

`ComboVerificationGate.Recorded` folds `policy.VerificationFor(tag)` off the
`UpdatePolicyContent` **the poller hands it**, so the gate is only ever as fresh as that read.
And the shape production always has is a verdict that lands *after* the pod started: a portal
runs for days, and `mw-combo-verify` records its verdict when a candidate is published.

`SelfUpdateHostedService.CreatePolicySource` used to end in
`DistinctUntilChanged(c => (c.Policy, c.RequireCiGreen))` — a leftover from the `Switch`-based
shape it outlived. Once the build watch moved out of the policy stream there was nothing left
for it to re-drive, so it no longer prevented a resubscribe; it filtered the **content**, and
that same stream is what `StartAsync` reads at decision time (`policy.Take(1)` off the
`Replay(1)`). Recording a verdict changes neither of those two fields, so the emission carrying
it was dropped and every later check kept deciding on the content as it stood at start-up. Every
other field went with it — `HeldTag`, `LastRolledTag`, an operator's edit on the Updates tab.

The gate was therefore recorded, rendered on the tab, and **unable to act on anything** — #2274's
"built, documented, tested, and called by nothing" with one extra step. Nothing de-duplicates the
content now. The trigger stream keeps its own `DistinctUntilChanged(content => content.Policy)`,
so a content change that is not a policy change still triggers nothing — including the poller's
own `LastCheckedAt`/`LastCheckVerdict` bookkeeping writes, which is why letting them through
cannot loop.

Pinned by `ComboGateRollTest.AVerdictRecordedAfterTheFirstCheck_IsHonouredByTheNextCheck` — the
verdict recorded after the first check is the advisory the next check records — whose second
check is driven by the **safety net** on purpose: a policy change would refresh the content even
with the defect present, because it moves the very field the filter keyed on.

## Cost, and why the walk is cheap

`VersionSelect.PickTargets` returns every eligible tag newest-first and the poller takes the
**newest admitted** one (`FirstRollable`): no package answer walks past a release, so there is no
per-candidate combo question left to ask and the selection consults no combo verdict at all. A
verdict is produced at most once per check — one docker run, on a host that can produce — about the
candidate actually chosen, and everywhere else `ComboVerificationGate.Recorded` is a pure read of
the policy content already in hand.

If that production comes back `Red`, the tick records it and rolls with the advisory.

Before policy `platform-rolls-independently-of-packages` the walk skipped a candidate recorded
`Red` and a freshly produced `Red` held the tick, so the two halves converged in one extra check;
that walk and that hold are retired.

## What is not provisioned, what it costs, and what closes it (#3848)

> **Measured 2026-09-13T18:45Z, read-only.** `repos/Systemorph/MeshWeaver/actions/variables`,
> `…/actions/secrets` and `…/dependabot/secrets`: **no `COMBO_*` entry of any kind, in either
> store.** Over the workflow's last **30** runs, `Verify` is `skipped` in **30 of 30** — not one
> instance has ever been verified. `Admin/UpdatePolicy.comboVerifications` is `[]` on **both** live
> instances.

### 🚨 The 16 GREEN runs verified exactly as much as the 14 red ones: nothing

Of those 30 runs, **14 are red** (the preflight naming the unprovisioned inputs) and **16 are
green** — and every one of the greens is the *no-candidate* branch: the triggering CD run concluded
`cancelled` (the one-pending-slot concurrency rule), so there was nothing to verify and the
`verdict` job exits 0 saying so. That branch is correct and it is the reason the gate does not cry
wolf on routine traffic. But it means **the workflow's conclusion is not a reading of whether
anything is verified**, and counting greens is the one way to get this wrong. The reading that
answers the question is `Verify`'s conclusion — `skipped` in all 30 — and the `instances=N`
denominator the verdict job prints.

### The provisioning, as a one-read task

Both credentials go in the **Actions** store of `Systemorph/MeshWeaver` and **only** there:
`combo-verify.yml` fires on `workflow_run` and `workflow_dispatch` and **never** on
`pull_request`, so its `secrets.` never resolve against the Dependabot store (there is no Dependabot
*variables* store at all). The **three** `AZURE_*` secrets (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`) and the two `FLEET_READER_*` secrets the same preflight asserts are
already provisioned for `main-cd`. Both the roster and module-source mapping are derived from the
fleet's deployment files, and there is no per-instance secret at all: what an instance needs is
provisioned ON the instance (*Provisioning an instance* below).

| Name | Kind | Value | Where it comes from |
|---|---|---|---|
| `SOURCES` | derived step output | space-separated `name=url`, e.g. `Plugins=https://github.com/Systemorph/MeshWeaver.Plugins` | the verifier's module-source mapping, derived from deployment-record `pluginRepos` entries with `isRegistrySource: true`; consumer registry mounts are excluded. Malformed records, conflicting URLs for one source name, or an empty union fail closed. |

🚨 **"One per instance" is answered by the lane, not by the reader.** The preflight prints the
derived roster, and each instance's verify job names that instance when it refuses the run's
identity. The hand-written value this page used to carry named the control instance and
the public instance — and the fleet's overlays declared more than that on the day it was specified, which
is the failure mode a derivation removes rather than a tidiness argument.

🚨 **The roster is a measured, fail-closed output.** Workflow run `36272707636` on 2026-09-26
derived five live installations — the build instance, the control instance, the public instance, an enterprise client's test
installation (`globex-test`) and an SME client instance — without a duplicate credential name. The same run stopped at the credential check because the
per-instance key and admin-token maps are absent; no instance was verified by that run. A missing
credential is not repaired by shrinking the roster. Duplicate names remain a red condition in the
deriver if the fleet declares them again. The earlier duplicate id was resolved by the
enterprise client's rename documented below; the fail-closed duplicate check remains for future declarations.

🚨 **The right answer in principle is a third one, and it is NOT IMPLEMENTED — do not reach for a flag
that looks like it.** Scoping the **denominator** is what fits here: an installation that can never
receive this candidate — because its overlay pins a registry declared `out-of-estate`, so the image
this lane verifies is not the image it runs — does not belong in the set this lane is measured over.
Nothing expresses that today, and **neither existing flag can stand in for it**:

| flag | what it actually means | why it cannot be used here |
|---|---|---|
| `instances.json` instance `state` | **liveness** — `live` / `not-installed` / `retired` (`ROSTER_STATES`) | it is the only exclusion lever that file has, and the installation in question IS live. Declaring it `not-installed` records a **falsehood** in order to obtain an exclusion. |
| the retention table's derived `out_of_scope` | the **lock** lane's registry scoping | `derive-combo-instances.py` never reads it. The build instance is live and `out_of_scope` and is still in the combo roster — the direct counter-example. |

So a `not-installed` exclusion is **not** a scope mechanism: it is a liveness declaration, and a live
installation must not be marked absent to obtain an exclusion. Building the real thing means an
explicit combo-scope declaration this derivation **consumes**, with its own self-test
arm. 🚨 And even then it is the **looser** direction, the only one of the three that can be silently
wrong: it SHRINKS the denominator, so an installation excluded by mistake is one this lane reports
nothing about while reading green — the very failure the derived roster replaced a hand-maintained
list to prevent.

`derive-combo-instances.py`'s self-test asserts those claims in the refusal — that denominator
scoping is not implemented, that the liveness states cannot express it, and that an accidental
exclusion would shrink the denominator silently.

### 🚨 The preflight asserts in the order that makes its red actionable

The preflight asserts only what it needs to read the deployment repositories; per-instance
provisioning is checked by each instance's `verify` job, which is red with a 401 naming both halves.
The first assertion checks `AZURE_*` and `FLEET_READER_*`; once those pass, derivation reads the
roster and source map. The next assertion checks that both derived outputs are present.

| step | asserts | why there |
|---|---|---|
| `assert` | `AZURE_*`, `FLEET_READER_*` — **and nothing else** | login and repository-read credentials required to attempt derivation |
| `derive` | — | emits non-empty roster and source outputs plus the roster digest, or fails on an unreadable/conflicting declaration |
| `roster` | derived roster, `SOURCES` and the digest | refuses an empty roster (an empty matrix skips `verify`, painted green), a missing source output or a missing digest, then hands over **slot numbers, the count and the digest — never the roster** (next section). There is no credential map to assert (#3848) |

Nothing became conditional and nothing can skip: no `if:` asks whether a secret is set, no step
carries `continue-on-error:`, and the roster is asserted unconditionally in the same `preflight`
job. A refused identity reds in that instance's verify job, by the instance's name.

`check-combo-verify.py` executes both assertion blocks' real shell — extracted from the workflow by
step id, never retyped — checks that the source output is wired from derivation through the
preflight into the verifier, and checks that the lander holds no credential. Its `--self-test` removes the assertions and requires the scenarios to
fail, so a preflight that asserts nothing cannot pass.

🚨 **No count appears in that sentence on purpose.** It used to name one, and adding a scenario made it
false — twice in one change set. A total in prose has no mechanism keeping it true, so the number lives
where it is derived: the script's own summary line, and `--self-test`'s own tally. The same applies to
the sibling count on [The Release Wave](/Doc/Architecture/TheReleaseWave), corrected for the same
reason.

Each `missing+=` and `absent+=` line names what to provision.
The `verdict` job at `:283-350` separates *no candidate* from *the preflight failed* from
*verification did not succeed*, so a red here reads as "verification never ran, provision X" rather
than as "verification failed". **That half of the lane is not the defect.**

### 🚨 The roster never crosses a job boundary — only slot numbers do

The preflight used to publish the derived roster as the job output `instances`, and the `verify`
matrix was `fromJSON(needs.preflight.outputs.instances)`. That stopped working the moment the fleet
included an installation whose name is private.

**What was measured (2026-10-10, the eight newest post-CD runs).** Each run listed exactly four jobs:
preflight `success`, the verdict `failure`, the audience job, and the ledger job `skipped`. **Not one
per-instance verify job existed in any of them.** The preflight's own log said why, as a *warning*
inside a job that concluded success:

```text
6 live installation(s) and 7 registry source(s) derived from 38 repository(ies); 1 declared not-live.
##[warning]Skip output 'instances' since it may contain secret.
Set output 'count'
##[warning]Skip output 'sources' since it may contain secret.
```

A client estate's roster lines live in the private roster, and the derivation registers every
identifier in it as a log mask so a red run cannot print one. **The runner refuses to pass any job
output that contains a masked value** — the whole output, not the masked part. `count` carried no
name and passed; the matrix received an empty string, `fromJSON` could not expand it, and the job
failed without creating a single child. The verdict printed `preflight=success verify=failure
instances=6` and gave no reason, so the run read as "an instance refused the candidate" about a run
that had contacted nobody. The same days' green runs were the *no candidate* branch (their triggering
CD runs were cancelled), so nothing in the workflow's history was a verification.

**The shape now.** Three values cross from `preflight` to `verify`, and none of them can contain a
name:

| output | value | why it is safe to hand over |
|---|---|---|
| `slots` | `[0,1,…,count-1]` | one opaque position per instance; the matrix is built from this |
| `count` | the number of live installations | the denominator |
| `digest` | HMAC-SHA256 over the roster and the source map, keyed with a secret both jobs hold and salted with the private roster, written as single digits joined by underscores (`4_0_7_…`) | lets a verify job prove it derived the *same* roster; without the key it cannot be recomputed, so the public value confirms no guessed name. The shape is the point: a hex string can spell a masked word and a plain decimal one can contain an all-digit id or a numeric secret, and an output containing either is dropped. A value can occur inside this one only if it itself alternates single digits with underscores |

Each `verify` job then runs the **same** derivation the preflight ran
(`derive-combo-instances.py --discover --slot N --expect-count … --expect-digest …`), takes the row at
its slot, and writes the installation's name, base URL and source map to `$GITHUB_ENV` — inside one
job, where a masked value is masked rather than dropped. The script refuses unless it derived the
preflight's count **and** the preflight's digest, so a fleet that changed between the two jobs is a
red naming the drift, never a verdict landed on a different installation than the one counted.

Re-deriving was chosen over handing the roster across as an artifact for one reason: this
repository's artifacts are public, and an artifact's name and the file names inside it are not
masked. For the same reason the per-instance job is named by its slot
(`Verify instance slot 3 against its roll target`) and so are its work-root artifact and the files in
it. Naming by slot hides the *name* only — what the files contain is the next paragraph's subject.
Which installation a public slot is can be read in that job's own log.

**A private row's module list never leaves the run.** The slot step also says whether its row is
private and writes that as `INSTANCE_PRIVATE`. *Public* is the thing that has to be shown: a row is
public only when the repository declaring it is in the committed
`.github/acr-retention/instances.json`, and the private roster does not name that installation by
id or host. Everything else is private — including every client row when the private-roster secret
is missing, so a lost secret cannot turn a client's rows public. The preflight counts a private row
without naming it, masks its derived host and id, and removes private identifiers from blocker and
exclusion lines, where an id too short to be a log mask would otherwise be printed. A registry
source that only an uncommitted repository's records declare is treated the same way: masked, and
shown as `<private>` in the log and the summary. For such a row the
lander prints the verdict and counts only: no name, no host, no response body, no module id, and the
verifier's own output goes to a file that is never uploaded. The failed-verification artifact
(`combo.json`, an installation's module list) is uploaded only when `INSTANCE_PRIVATE` is literally
`false`, so an unset or unexpected value uploads nothing. The verdict is still landed on the
instance in full, which is where its owner reads it. This is the safe default for a public
repository; what a client estate's diagnostics should look like beyond it is the estate owner's call.

**And the verdict counts jobs.** `needs.verify.result` is one word for the whole matrix: it cannot
tell "six jobs ran and one failed" from "the matrix never expanded", and it reads `success` over
however many jobs the matrix happened to create. So the `verdict` job asks the run which
per-instance jobs it holds — by the exact name the `verify` job declares — and fails unless the
number created **and** the number that concluded success both equal the preflight's count. Zero
created has its own message, naming the unexpanded matrix and where to look for the dropped output.

`check-combo-verify.py` holds all of it on every platform pull request, because this workflow never
runs on one. Structurally: the preflight publishes exactly those three outputs, the matrix is built
from `slots`, nothing in `verify` reads a name out of the matrix or out of a preflight output, the
slot is resolved before the lander with the count and digest passed, and the verdict counts jobs
unconditionally. Behaviourally: it executes the roster step's real shell and applies the runner's
own rule to every value written — a value *containing* any name, host or source of the roster is one
the runner would drop — and it executes the verdict step's real shell over the job counts, including
the measured case (instances derived, no job created). Its `--self-test` re-publishes the roster,
names the job after the installation, removes the slot step and guts the verdict, and requires each
to be refused. `derive-combo-instances.py --self-test` drives the slot resolution's refusals.

### What being UNVERIFIED costs today: nothing gates on it

Two independent reasons, and both were measured rather than assumed:

1. **By design, an absent verdict neither clears nor refuses.** `ComboClearanceKind.NotVerified`
   "refuses nothing, because refusing on it would freeze every instance in the fleet on the day this
   gate shipped" (`ComboClearance.cs:20-30`). When this was measured a `Refused` — a recorded
   **Red** — removed a candidate from the walk and held a roll; under
   policy `platform-rolls-independently-of-packages` it does neither and is recorded as an advisory
   (`SelfUpdateHostedService.ComboThenApply`). An
   unverified roll is **applied**, with `LogWarning("[SelfUpdate] rolled {Tag} WITHOUT combo
   clearance")` and the verdict marked `Unverified` (`Qualify`). Of the other two readers,
   `PlatformUpdateStatus.Derive` reads a Red as an advisory beside *update available*, and
   `UpdatePolicySettingsTab` **renders all three kinds** — Green, Red and
   NotVerifiable each get their own rendering, with the verdict's caveats surfaced on every one —
   so nothing is hidden from an operator, there is simply nothing recorded to show.
2. **The one consumer is switched off on both live instances.** `Admin/UpdatePolicy` on
   the control instance and on the public instance both read
   `lastCheckVerdict: "updates are disabled on this install (Admin/UpdatePolicy = None); the
   registry was not listed."` So the self-update poller — the only thing in this repository that
   consults a verdict at decision time — is not deciding anything to begin with.

That is **not** an argument for leaving it unprovisioned: the gate exists so that the day
self-update is switched back on, the modules a candidate cannot serve are **named before and on the
roll** rather than discovered at boot. It is an argument about *priority* — this is a latent gate, not a live incident, and it
should be read that way when it is scheduled against work that is bleeding.

### 🚨 The instance roster IS derived — `vars.COMBO_VERIFY_INSTANCES` no longer exists

#3842 rules out hand-maintained pins, and `vars.COMBO_VERIFY_INSTANCES` was one. The issue's own
remainder list recorded "enumerate instances at run time" as *depending* on a credential for reading
the control instance's `Deployments/*` records. **That dependency never held for the route this
repository already uses**, and the derivation is now in the lane.

`.github/scripts/derive-combo-instances.py` reads the fleet's deployment overlays — every
`Hosting__Deployment:` plus its `ingress.host` — **through `lock-pinned-digests.py`'s own AXIS 3
extractor**, imported rather than copied, so the set this lane verifies and the set the nightly lock
protects cannot disagree about what an installation is. It needs only the read-only **fleet-reader**
GitHub App, whose two secrets this preflight already asserted; the preflight now also checks out the
tree and mints that App's token (`combo-verify.yml:148-160`), which is the one structural change the
move required — the job previously had neither.

`.github/acr-retention/instances.json` is the only thing that removes an installation from the
roster, and only by declaring it `retired` or `not-installed` **with a reason**. 🚨 **The overlays
are the denominator and that file only explains an absence**: an installation it does not mention is
LIVE, so forgetting an entry makes the lane stricter (one more instance demanding a credential),
never looser — the only direction a hand-maintained file may fail in.

#### 🚨 …but a line that has gone WRONG fails in the looser direction, and it is silent by construction

"Forgetting an entry is the stricter mistake" is a statement about an **absent** line. It says
nothing about a line that was true when it was written and is not any more — and that one removes a
live installation from every denominator derived from this file, with nothing anywhere contradicting
it, because a non-live installation was never asked anything.

Measured: an SME client instance's entry read *"never installed — fabrikam.example.com has no DNS
record (measured 2026-09-12)"* and ended *"Delete this entry the day it is provisioned."* The
instance was provisioned 2026-09-16 and answered `/api/version` from then on. Five days later the
line still stood, so the instance was outside this lane's roster **and** outside the nightly lock's AXIS 3 protected
set, and no run of either was red about it. The entry even named its own expiry condition; nothing
evaluated it.

**A non-live declaration is now re-measured against the one question that can falsify it — does a
portal answer at that host?** `build_instances` probes an exempted installation through the same
probe seam it already takes, and an ANSWER is a blocker naming the line to delete. The reading is
the returned **commit**, never the absence of an error, so `derive-combo-instances.py` — which
passes a probe that answers nothing — stays free of an HTTPS call to every portal in the fleet and
the arm is inert there by construction: one lane does the network. An exempted installation whose
overlay names no host (an enterprise client's hand-over template, which still reads `host: "TODO"`) cannot be falsified
this way and is left to the roster's own stale-entry check, so the cost in the fleet today is zero
calls.

`lock-pinned-digests.py --self-test` ARM 25b drives both halves, and both were proven able to fail:
disabling the check reports *"an exemption for a portal that ANSWERS did not red"*; making it fire
regardless of the answer reports the negative control **and** ARM 24 independently.

🚨 **Every way the derivation could come back empty is a RED, not a shorter answer.** An empty roster
would produce an empty matrix, an empty matrix SKIPS the verify job, and GitHub paints a skipped job
the same colour as a passed one — which is the whole of #3848. So the script exits 1, naming the
cause, on: a repository whose overlays could not be read (NOBODY LOOKED is not a measured zero); an
installation declared by two overlays; a roster entry naming an installation no overlay declares; a
live installation with no `ingress.host`; two installations resolving to the same host; and zero
live installations at all. It also drives the overlay extractor over two known fixtures on **every**
run, so "the extractor stopped matching" can never arrive wearing "the fleet declares no
installations". Three independent layers refuse a zero — the script, the preflight's roster step,
and the `verdict` job's `COUNT < 1` arm — and `check-combo-verify.py` plus
`derive-combo-instances.py --self-test` drive all of them on every pull request. A fourth refuses
the zero that is not in the roster at all: a non-empty roster whose matrix created no job (see
*The roster never crosses a job boundary* above).

### The verifier derives its module-source map from deployment records

The preflight derives `SOURCES` from the union of `pluginRepos` mounts in the scanned
`Hosting/Deployment` records, retaining only entries whose `isRegistrySource` is `true`. That flag
distinguishes a repository that supplies modules (whose URL belongs in `--source`) from a consumer
mount, whose URL is a registry endpoint. The mount's `name` is the `PackageCoordinate.SourceName`
stamped on installed modules, and its URL is the repository `InstanceComboAssembler` needs to
materialize those modules (`ComboAssembly.SourceRepositories`, consumed at
`InstanceComboAssembler.cs:310`).

The roster and source map use the same record scan. Source names are compared case-insensitively,
matching `SourceRepositories` in the assembler: casing variants that point to the same URL collapse
to one stable spelling, while the same name mapped to different URLs, malformed registry-source
data, an unreadable deployment record, or a fleet with no registry-source mounts fails the
preflight. No repository variable can silently omit a source or point the verifier at a stale
repository. In
`Systemorph/Memex`, `check-record-renders-overlay.py` also checks that registry-source records and
`pluginCatalog.sources` agree by name, URL and ref in both directions.

### The derived inputs are not provisioning, and per-instance provisioning still gates verification

The preflight derives the roster and source map; whether each instance accepts the run's identity is
decided in that instance's `verify` job, not in the preflight.
Run `36272707636` on 2026-09-26 derived five live instances — the build instance, the control instance, the public instance, an enterprise client's test
installation (`globex-test`) and an SME client instance — then stopped because the key and admin-token
maps it then required were not provisioned. Those maps no longer exist: each instance is reached as
the run's own identity (*Provisioning an instance* below). The derivation does not imply any instance
was verified.

### The duplicate-name blocker

The duplicate-name blocker that preceded provisioning was resolved by renaming an enterprise
client's installation to `globex-test` (#3848). Qualifying the roster by `repo:id` would have
required a grant on an installation outside this fleet's control; the rename kept the name-keyed
verify jobs unambiguous.

### The rename — the enterprise client's installation is `globex-test`

The first way out was taken (#3848). The control instance keeps its id; the enterprise client's
installation at globex.example.com answers to **`globex-test`** (a pull request in the client's
deployments repository). The id comes from that record's own `purpose` (a test environment); `globex`
was not available (the client's hand-over template declares it) and `globex-memex` is a retired
registry id.

**What moved is the identity and nothing else.** `Hosting__Deployment` (overlay and record
`extraPortalConfig`), the record's id and file (`mesh/Deployments/globex-test.json`) and that
repository's `envs.json` `deployment` changed. The namespace, the Helm release, the overlay
directory (`deployments/aks/memex/`), the database, the vault prefix `memex-`, the host and the
registry instance id (`pluginCatalog.instanceId: globex`) did not. The registry authenticates an
instance key by the instance it was registered for, never by `Hosting__Deployment`, so nothing is
re-registered. Because the record id no longer equals the namespace, the client deployments repository's
`check-record-renders-overlay.py` now pairs a record with its overlay by the record's `configPath`.

What the rename changes on the running instance, which has no `Hosting:ReportTo` and lists
`Hosting/PlatformBuilds` itself (so it is its own control plane for both channels):

- its module inventory is filed at `Ops/Modules/globex-test` instead of `Ops/Modules/<old-id>`;
- its self-update hand-over (the Local route) announces `deployment: globex-test`, and a
  `Hosting/InstanceAction` filed on its own mesh must target that id (`RecycleRunner` refuses a
  target that is not the instance's own `Hosting:Deployment`).

No mesh the fleet can read syncs that repository's `mesh/Deployments` — the control instance's and
the public instance's `Deployments/_GitSync` both read `Systemorph/Memex` — so the file rename is
not a delete-and-create anywhere else.

The roster the derivation then prints is five installations: the build instance, the control instance, the public instance, an enterprise client's test
installation (`globex-test`) and an SME client instance. The refusal and its self-test arm stay: they guard the mechanism, and the next
deployments repository to declare a taken name meets the same red.

### Provisioning an instance

There is nothing to issue and nothing to store in this repository (#3848). The lane used to need an
`mwi_` instance key and a global admin's `mw_` token per instance in two Actions secrets; neither can
be read back, so for a month the lane waited on twelve hand-issued values and every roll stayed
UNVERIFIED. It now authenticates as the run itself, and an instance accepts that identity when it
holds **both** of these:

1. **The audience, as declared config.** `Plugins:Registry:BuildPrincipalAudience` = the instance's
   own base URL (for example `https://memex.systemorph.com`), declared in the deployment record's
   `extraPortalConfig` as `Plugins__Registry__BuildPrincipalAudience` and mirrored by its overlay.
   With no audience configured the portal refuses every build token by design
   (`InstanceRegistryAuthenticator.AuthenticateBuild`), because accepting any audience would trust
   every GitHub Actions run that pointed a token at anything.
2. **The grant — declared in the same record, or written as a node.** The rule is
   `repository: Systemorph/MeshWeaver`, its immutable `repositoryId` and `repositoryOwnerId`, `events`
   `workflow_run` and `workflow_dispatch` → `verify`, both `eventRefs` restricted to
   `refs/heads/main`, and `scopes: ["verify:combo"]` and nothing else. It grants reading the release
   inputs and recording a verdict through `POST /api/plugins/combo-verification`; it cannot install,
   apply an update, read user content, edit access or change the update policy
   ([Access control](../AccessControl) → Build principals). It has two homes:

   - **Declared** (policy `declared-not-derived`), beside the audience in `extraPortalConfig`, which
     is how an estate provisions every one of its instances with one merge:

     ```text
     Plugins__Registry__BuildPrincipals__0__Repository             Systemorph/MeshWeaver
     Plugins__Registry__BuildPrincipals__0__RepositoryId           <repository_id>
     Plugins__Registry__BuildPrincipals__0__RepositoryOwnerId      <repository_owner_id>
     Plugins__Registry__BuildPrincipals__0__Events__workflow_run__0        verify
     Plugins__Registry__BuildPrincipals__0__Events__workflow_dispatch__0   verify
     Plugins__Registry__BuildPrincipals__0__EventRefs__workflow_run__0       refs/heads/main
     Plugins__Registry__BuildPrincipals__0__EventRefs__workflow_dispatch__0  refs/heads/main
     Plugins__Registry__BuildPrincipals__0__Scopes__0              verify:combo
     ```

   - **A node**, `Admin/_BuildPrincipal/systemorph--meshweaver`, created by a global admin of that
     instance. A node that already exists keeps working unchanged.

#### The declared form: stricter than a node, visible, and overridden by one

The grant used to exist only as a node, created by hand on each instance by whoever held a global
admin's session there. An instance nobody could reach that way never got it, and its verify job
stayed red on a 401 with nothing in any repository to change. The audience was already declared in
the record; the grant now travels the same route.

- **Stricter than a node.** `DeclaredBuildPrincipals` refuses a declaration that leaves out either
  immutable id, lists an event without a ref allow-list, lists a verb no declared scope carries, or
  carries a scope other than `verify:combo`. Publishing or fetching a source stays an act of a
  global admin on a node. A declaration that breaks a rule is refused **whole** — it is never
  narrowed into something that passes — and so is every declaration of a repository declared twice.
  An unknown key refuses it too, so a typo cannot silently drop a restriction.
- **Visible.** A declared principal is in no node, so `search nodeType:BuildPrincipal` no longer
  lists everything an instance trusts. The public `/health` body carries a `build_principals` line
  that prints whatever its status: the repository, both ids, each event with its refs and the
  scopes of every declaration in force; each refused declaration with the rule it broke (Degraded);
  and, when principals are declared but no audience is, that the whole leg is off (Degraded). It
  names declarations only — nodes are not listed there.
- **Overridden by a node.** The authenticator reads the node first and consults the declaration
  only when no node exists for the repository. A node narrower than the declaration narrows it, and
  a node carrying `requestedAction: Revoke` stops a declared principal on the next request, with no
  roll. An unreadable node is answered retryable and never falls through to the declaration,
  because a revoking node may be exactly what could not be read.
- **Nothing is materialised.** The declaration is read from configuration when a token is decided.
  No node is written at boot, so there is no importer and nothing that could prune one; removing
  the declaration from the record removes the trust at the next roll, and only that does.

A declaration reaches a portal the way any record change does: the record change opens a
record-change Reconcile on the control instance, and the portal reads the new configuration when
its pods are replaced. The portal must run an image that knows the section; an older image ignores
the keys and keeps answering 401 until it rolls.

Revoking a node is a node write and takes effect on the next request.

The lander mints a fresh token before **every** instance call (the verification between the reads
and the landing can outlive a token minted once) and lands through the recording route, which merges
by `UpdatePolicyNodeType.RecordVerification` and answers 200 only once `Admin/UpdatePolicy` carries
the verdict — so the merge rule exists once, in the portal, instead of being re-implemented in jq
over a raw mesh patch. A 401 from any of the three calls names both halves above for that instance;
the portal does not tell a refused caller which half is missing.

#### A landing that does not finish in time is a 504 naming the phase

The recording route gives the write and its confirmation **one** 30-second deadline. When it
expires, the route answers **504** with
`{ "error": "Timed out: …", "phase": "write" | "confirm", "candidateTag": "…" }` and logs one
Warning naming the verdict, the candidate, the build's repository and run, and the phase:

- **`write`**: the write to `Admin/UpdatePolicy` had not completed.
- **`confirm`**: the write completed, but `Admin/UpdatePolicy` never showed this exact verdict.

🚨 In **both** phases, whether the verdict is recorded is **unknown**. Answering ends this request's
subscription. It does not cancel a write that is already queued, so the verdict can still land
after the 504. Re-read `Admin/UpdatePolicy`, or re-post: the record is an upsert by candidate tag,
so a re-post is idempotent.

Only this route's own deadline is answered. It is `Timeout`'s fallback, not a `Catch` of
`TimeoutException`. Any other fault still reaches the portal's exception handler as a 500. That
includes a `TimeoutException` raised upstream, for example by a write's own bound. A fault that is
not this deadline is a defect, and mapping it here would hide it.

Before this change the deadline escaped as a bare `TimeoutException`. Rx creates that exception
without throwing it, so it carries no stack frames. The result was an unhandled 500 whose log line
named neither the route nor the phase (#6790). In that incident the verifier's POST answered 500
after 31 s. The node's version history, read through the version written 9 minutes after the
POST, shows no write of that verdict. The versions on either side of the POST differ only in the
poller's `lastCheckedAt`, and the tag kept an earlier run's verdict. So the write was the phase
that stalled, and nothing in the response could say so. The endpoint's composition
(`ReleaseGateEndpoints.RecordAndConfirm`) is pinned on virtual time by
`ComboVerificationDeadlineTest`, which has three controls:

- a confirmed write is answered;
- a fault that is not a deadline still propagates;
- a `TimeoutException` raised upstream still propagates.

`check-combo-verify.py` holds this shape: it requires the token request, the audience, the recording
route, the confirmed landing and a mint before every call, refuses any return of the instance key,
the admin token or the raw patch, and executes the lander once without an OIDC request URL to prove
it stops red naming `id-token: write`.

## The recorded commit is the reference, and a retired package is reported, not failed (#3848)

Policy `record-served-commit-on-install` ([Policy Not Prose](../PolicyNotProse)). Before it, every roster instance
that the run's identity reached came back `NotVerifiable` with **zero modules verified**
(memex: 86 modules, memex-cloud: 124; run 38416709107). The cause was not the candidate. The
assembler refuses the whole root when any one module cannot be pinned, and two classes of module
could never be pinned:

| class | before | now |
|---|---|---|
| an install record with a `moduleVersion` but no exact commit | fetched at `HEAD` and proved against the hash, which fails for every package changed since its install | the registry's install response names the commit it served, the record stores it, and the module is fetched **at that commit** |
| a package whose source folder has left its repository | `Failed`, which refused the whole root | **retired, not verifiable**: reported, and it does not fail the verification |

### How the commit reaches the record

1. `POST /api/plugins/files` answers `{ files, commit }`. `commit` is the snapshot sha the
   registry's source read the folder at (`IPackageSource.FetchPackage` → `PackageFetch.Commit`).
   An answer from an older registry carries no `commit`, and a value that is not a full sha is
   never written.
2. The install funnel (`CatalogLayoutAreas.InstallOrUpdate`: full install and incremental update
   alike) records `PackageFetch.InstalledFromRef(sourceRef)` as the record's `installedFromRef`.
   That value is the served commit when there is one, and otherwise the ref the install asked
   for, as before. An incremental update that carries only removals still makes the (empty) read,
   because that read is what names the commit.
3. `InstanceComboAssembler` already fetched a sha-shaped `installedFromRef` directly. It now also
   **proves** the recorded `moduleVersion` there. Content that diverges from its recorded commit
   fails (`moduleVersion mismatch at the recorded commit`), and no `--allow-moving` excuses it,
   because a commit does not move.

### One snapshot for the manifest and the delta (#6718)

An incremental update reads TWICE: first `manifest.lock`, to diff it against the record, then the
changed files. Both reads used to ask for the moving ref, so a ref that advanced from commit A to B
between them produced a record pairing A's module version and file map with B's files and B's
`installedFromRef`. The verifier then refused the record at B, loudly, but the partition already
held a mix of two snapshots.

- **The manifest read's commit pins the delta read.** `CatalogLayoutAreas.IncrementalUpdate` takes
  the commit the first read was served at (`SnapshotOf`) and asks for the delta **at that commit**;
  the record states it too. A source that names no commit (an older registry) reads the ref twice,
  as before, because there is nothing to pin to.
- **A delta served at any other commit is refused** (`SameSnapshot`), naming both commits. The
  caller falls back to a full install, which reads manifest and files in ONE fetch. So a source that
  ignores the requested commit can cost a full install, and can never produce a mixed record.
- **The registry honours the commit as a snapshot token.** `POST /api/plugins/files` reads its
  configured ref, except when `ref` is a full commit sha **reachable from** that configured ref
  (`IPackageSource.PublishesCommit`: a GitHub compare `ahead`/`identical`, or
  `git merge-base --is-ancestor` for a local source). A commit that exists only on another branch,
  an unknown sha, or a source that cannot establish ancestry reads the configured ref, as before,
  so the token can never become a way to read a package at an unpublished commit. Every decorator
  forwards `PublishesCommit`, because the interface default establishes nothing.

Pinned by `IncrementalUpdateReadsOneSnapshotTest`: a source whose ref advances between the two
reads and that honours the commit (the delta lands on A), the same advance at a source that ignores
the requested commit (refused, then one full read at B), and the registry's ref choice for a
reachable sha, an unreachable sha and a branch name.

**No backfill.** A record written before the decision carries the requested ref (`main`, `HEAD`)
and keeps the old path (fetch the moving ref and prove the hash) until its next update restamps
it. Nothing rewrites existing records.

### Retired, not verifiable

`ModuleAssemblyStatus` is an open vocabulary of string constants (policy
`open-vocabulary-string-constants`). It replaced the enum of the same name, and the manifest's
wire spelling is unchanged. The decision adds `Retired`:

- A module is `Retired` only when its folder is absent at the ref it was fetched at **and** at the
  source repository's `HEAD`. That takes one extra `HEAD` read per fetch group, and only when a
  member was absent. A folder absent at the recorded ref but **present at `HEAD`** stays `Failed`:
  the record is wrong, and the package is not gone. A `HEAD` read that fails proves nothing, so
  the module stays `Failed` and the line says so.
- 🚨 **Absence is read only from a COMPLETE listing (#6742).** GitHub truncates a large recursive
  tree read and still answers HTTP 200; the snapshot then carries `ListingIsComplete: false`, and
  a folder missing from it may simply be in the part that was not listed. So a module is retired
  only when BOTH snapshots — at its fetch ref and at `HEAD` — are complete. A folder missing from a
  truncated tree at either ref stays `Failed` with an `indeterminate listing` reason, so the
  verdict cannot go Green on a partial listing. The same rule holds when the fetch ref already was
  `HEAD`: that one snapshot must be complete. This is the rule the importer's prune already
  follows ([Prune Requires a Complete Listing](../PruneRequiresACompleteListing)).
- 🚨 **Presence is not proof either (#7316).** A module whose files come from a truncated listing
  is never `Materialized`, even when the listing names some of its files: the files it left out
  are invisible downstream, and a matching `moduleVersion` proves only the `manifest.lock` the
  listing happened to name. It stays `Failed` with an `indeterminate listing` reason.
- `ComboAssemblyReport.Success` counts a retired module as not failing. A combo whose every module
  is retired is still **not** a success, because the gate would otherwise run over an empty root.
- The verdict carries retired modules on their own list, `ComboVerification.RetiredModules`. They
  are absent from `Modules`, so they make a verdict neither Red nor NotVerifiable. A separate list
  was chosen deliberately over a new `ModuleVerificationOutcome` member. A portal on an older image
  deserializes the verdict when the build posts it. An unknown enum member would refuse the whole
  verdict there, while an unknown property is ignored.
- Surfaces: the Updates tab lists them under the localized heading `ui.updateRetiredModules`
  ("Retired, not verifiable" / "Stillgelegt, nicht prüfbar"). The clearance sentence names them.
  The recording route refuses a verdict that lists a module both as verified and as retired.

Pinned by `ComboVerifiesTheRecordedCommitTest` (the assembler and verifier over an in-memory
repository whose `HEAD` has moved) and `InstallRecordsTheServedCommitTest` (the registry's answer
and the install record). Each test file carries its own negative controls.

## The module half of the combo is composed too (#4413)

An instance runs two halves of every installed package: its **content** (node definitions, at the
recorded commit) and, for most packages, its **compiled module** (`MeshWeaver.AI`,
`MeshWeaver.Blazor.Chat`, …). The portal image carries only its boot modules; every other module
is landed per package from the registry. The combo records the second half as
`package.compiledModule` on each install record.

Until #4413 the verifier composed only the first half. It materialised the content and ran
`mw-plugin-test` with no `--module`, and the tester image ships no module of its own. So no
`Skill` or `Agent` NodeType was registered. `Store`, first in install order, failed
`NodeType(s) not registered: Skill`, and every later package failed `NodeType 'Store/Plugin' is not
registered`. Measured on run `40002536862`, slot 0: 54 packages, 0 passed, 54 failed. The verdict
blamed the content, but the cause was the instrument. A tree holding one `Space` package with one
`Skill` node reproduces it locally with the tester alone, in the same words.

What the lane does now:

1. **The lander fetches the sealed module set.** `combo-verify-instance.sh` reads the sources of
   every package that declares a compiled module. For each source it runs
   `compose-sealed-modules.sh --packages '*'` against the publication sealed for the candidate's
   framework identity. It uses the same share (`vars.BAKE_PUBLISH_TARGETS`) and the same OIDC
   login as main-cd's `published-modules` job. The rule is the one every gate follows: the
   publication is the unit of consistency, and there is no registry fallback. A source with no seal
   for the identity is red at this step.
2. **The verifier loads only what the combo declares.** `ComboModuleSet` reads the bundles with
   the platform's own `BundleReader`, which applies the closure, static-asset and native rules the
   portal's landing applies. It lands each declared module under `modules/<Name>/`. A sealed module
   the instance does not install is not loaded, because it would register types the instance lacks.
   A declared module that no seal carries is not loaded either, which matches the producing
   repository's own gate (its seal holds only the modules its content compiles against). The
   verdict names those modules in a caveat.
3. **The gate mounts the modules and passes each as `--module`.** `DockerImageGate.RunArgs` mounts
   the composed root read-only at `/ext` and appends `--module /ext/modules/<Name>/<Name>.dll`, the
   same argv the node-repo gate lane builds.

### What the composed set refuses, and what it only names

The verifier treats each of these as a broken instrument: it exits with code 3, writes no verdict,
and lands nothing. A run without the modules, or with a corrupt set, would land a content-shaped
Red on the instance.

- The combo declares compiled modules and the verifier got no `--module-bundles`.
- The fetched directories exist but carry none of the declared modules (a fetch that wrote
  nothing).
- **One build per assembly name across the whole set.** Every `MeshWeaver*.dll` in the selected
  closures is hashed. The same file name arriving as two different builds from two different
  entry modules is refused, and the error names both modules and both digests. The loader would
  bind the name to whichever copy it saw first, so the gate would judge an arbitrary build. This
  is the refusal `node-repo-gate.yml` makes over its own composed set (#3732). Individually sealed
  sources do not prove that their union agrees.
- A module name, closure file name, asset path or native path that is not a flat,
  non-traversing name. It is refused before anything is written, in the order
  `ModuleLandingService` uses.
- A bundle that declares static assets or native payloads it does not carry. The platform's
  readers answer *empty* for an incomplete section, so the declared count is checked against the
  read count. A module that loads and then 404s its assets would otherwise be judged as complete.

The verdict NAMES these in its caveats, and they do not change its kind:

- **Declared modules that no fetched seal carries.** They are not loaded. A publication seals only
  its own repository's modules, so refusing here would leave every slot without a verdict for
  good. The producing repository's own gate runs its content against the same partial set.
- **Version divergence.** The composed bytes are the set sealed for the *candidate's* identity,
  which is what the instance lands once it rolls there. That is not necessarily what it runs
  today. Each module whose bundle version differs from the install record's released version
  (`ReleasedVersion`, falling back to `Version` on legacy records) is named, for example
  `MeshWeaver.AI: installed 1.37.12, composed 1.38.0`. Pinning the gate to the installed module
  version would need a per-version registry read with an instance key, and this lane holds none
  (#3848).

On a Green with an automatically allocated work root, the verifier deletes both the work root and
the module set beside it (`<work root>.modules`). On anything else it keeps both for inspection.

All of this is pinned by `ComboComposesTheModuleHalfTest`. Each refusal has a negative control:
the test fails when its guard is disabled, and the argv test fails when `RunArgs` drops the
`--module` entries.

## Known boundary: the combo moves

A verdict names the `ComboReadAt` of the snapshot it verified. An instance whose modules sync forward
after a Green was recorded is no longer the instance that verdict is about. Today the verdict's own
`Caveats` are the only signal of that; the gate does not re-read the combo to compare, because doing
so would put a mesh query back into the roll decision that the wedges-to-zero invariant keeps out.
Re-verifying a candidate is an upsert by tag, so the cure is to produce a fresh verdict — which is
also what clears a stale `Red`.

## Related

- [Candidate Release Protocol](/Doc/Architecture/CandidateReleaseProtocol) — producing a verdict,
  the `--platform` rule, and the operator runbook.
- [Deployment](/Doc/Architecture/Deployment) — the routes a version actually rolls along.
- [Roll Selection](/Doc/Architecture/RollSelection) — how the candidate walk chooses, why a
  recorded Red removes a candidate, and why an instance-side failure is outside a selector's reach.
- [Guards and Unknown States](/Doc/Architecture/GuardsAndUnknownStates) — the general form of "an
  unanswered check is not a passing one".
