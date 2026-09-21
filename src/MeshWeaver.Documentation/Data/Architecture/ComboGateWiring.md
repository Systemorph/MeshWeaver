---
Name: Combo Gate Wiring
Category: Architecture
Description: How the self-update decision consults the combo verification — the three verdicts, what an instance does on each, and why "could not find out" is neither a pass nor a refusal.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="m9 12 2 2 4-4"/></svg>
---

# Combo Gate Wiring

An image being **newer** says nothing about whether it can **serve** what an instance already runs.
A framework-identity change invalidates the whole assembly cache by design, and an optional
parameter added to a record's primary constructor *replaces* the signature — so a portal can roll to
a build that passed every CI job, has a sealed content bake, links every landed module, and still
aborts at boot with a `MissingMethodException` because a landed module binds a method that no longer
exists.

That is not hypothetical. `memex.systemorph.com` was trapped between two failing states: rolling the
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
                          ┌──────────────────────────┴───────────────────────┐
                          │ per candidate (the WALK — pure, no IO)           │
                          │  availability gate accepts  AND  combo not RED   │
                          └──────────────────────────┬───────────────────────┘
                                                     ▼
                                              chosen candidate
                                                     │
                       ReleaseAvailabilityService.IsUpdatable  ─── hold ──► HELD
                                                     │ clear
                                                     ▼
                        ComboVerificationGate.Clearance(policy, tag, imageRef)
                                                     │
        ┌────────────────────────┬───────────────────┴──────────────────┬────────────────────────┐
        ▼                        ▼                                      ▼                        ▼
     Cleared                  Refused                              Unverifiable             NotVerified
     (Green)                   (Red)                             (NotVerifiable)         (no verdict at all)
        │                        │                                      │                        │
      roll               HOLD, modules named                      neither — roll, recorded as UNVERIFIED
```

The two gates answer **different questions** and are deliberately independent: the availability gate
asks *"does a usable artifact exist for the target?"*, the combo gate asks *"can the target serve
what this instance has already landed?"*. Passing one says nothing about the other.

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
- `Admin/UpdatePolicy.comboVerifications` was `[]` on memex-cloud, and both portals said so in their
  own words: *"no combo-gate runner is registered on this host, so it produces no verdicts of its
  own — they are landed here by mw-combo-verify."*

So every self-update rolled `UNVERIFIED`, said so in the log, and **applied the update anyway**. The
gate built to prevent a `MissingMethodException`-at-boot roll was never once consulted with data.

`.github/workflows/combo-verify.yml` is the producer. It runs off the CD workflow's completion, and
per instance it does exactly the three steps
[Candidate Release Protocol](/Doc/Architecture/CandidateReleaseProtocol) describes:

1. **Ask the instance what it would roll to** — `GET /api/plugins/roll-target`. The candidate is
   asked OF the instance rather than derived in CI, because "the newest tag" is not the question the
   gate answers: `ReleaseAvailabilityService` already walks the completeness rule and names the
   release this environment would actually take. Re-deriving it would be a second rule.
2. **Ask the instance what it runs** — `GET /api/plugins/combo`, added by #3544 beside its two
   `mwi_`-gated siblings. Its body is an `InstanceCombo` serialized with
   `InstanceComboAssembler.Json`, i.e. it **is** `combo.json`, byte-for-byte what `mw-combo-verify`
   deserializes. Before it existed, `InstanceComboReader` had no HTTP, MCP or layout surface at all
   and the producer's first input was simply unobtainable from a running portal.
3. **Verify, then LAND the verdict** — `mw-combo-verify combo.json <acr>/memex-portal-ai:<candidate>`,
   then a read-merge-write onto `Admin/UpdatePolicy` through `/api/mesh/get` + `/api/mesh/patch`.

🚨 **The verdict is landed BEFORE the job's exit code is decided.** A Red that never reaches the
instance is worse than no verdict at all — the instance's own gate would then clear a candidate that
run had already proved it cannot serve.

🚨 **An RFC 7396 merge patch replaces an array wholesale**, so the whole list is sent and the merge
rule is not reinvented in CI: upsert by `candidateTag` (case-insensitive), newest first, capped at
`MaxRecordedVerifications` = 8 — the rule `UpdatePolicyNodeType.RecordVerification` applies
in-process.

🚨 **Green is the only pass.** A `Red` fails the job (delivery promoted a candidate a live instance
must refuse) and a `NotVerifiable` fails it too — that outcome means *nothing was checked*, which is
"the gate never ran" wearing the colour of "the gate passed".

### Why it is its own workflow, and why its preflight is red until provisioned

The lane needs two credentials per instance that nothing in CI holds today: an `mwi_`
instance-registry key (reads `roll-target` and `combo`) and an `mw_` API token of a **global admin**
on that instance (lands the verdict; the token authenticates as its owner and carries no scope of
its own). The declaration of *which* instances to verify does not exist in this repo either.

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
| `Red` | `Refused` | **HOLD.** Every failing module is named on `Admin/UpdatePolicy`, logged at Error, and re-decided from scratch on the next check. |
| `NotVerifiable` | `Unverifiable` | **Neither.** No clearance, no refusal. |
| *(none)* | `NotVerified` | **Neither**, with a different sentence: nothing has asked. |

### 🚨 Only a Green clears

This is the property that makes the gate a gate. There is deliberately **no configuration key, no
missing-service branch and no `catch`** that can produce `Cleared`. An unregistered gate, an
unregistered runner, a producer that faulted, a producer that timed out, and an unknown future
`ComboVerdictKind` member all land on a state that grants nothing — and the switch in
`ComboClearance.For` names Green and Red *explicitly*, with everything else falling through, so a
member appended tomorrow cannot silently become a pass.

`SelfUpdateOptions.AllowUnverifiedRoll` waives the *availability* gate that could not run. It does
**not** waive a combo Red: that gate ran, and a key that could wave away a produced refusal would be
exactly the skip-trapdoor this area exists to keep out.

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
- it never refuses — the roll rests on the other gates, which is where it rested before this gate
  existed;
- the check verdict is **qualified** — `UpdatePolicyContent.LastCheckVerdict` reads
  `applied update X … UNVERIFIED — <why>`, durably, on the node the Updates tab renders;
- and a patch actually issued without clearance logs at **Warning**, naming the reason.

The durable half is not optional. A log line depends on a per-category log level a deployment may
never have set — which is exactly how an install sat three builds behind for seven hours with
nothing in the product able to say so (#2553). A node write does not.

`NotVerifiable` and *no verdict at all* behave identically and read differently, on purpose: "the
gate ran and could not answer" and "nothing has ever asked" are different incidents with different
fixes, and an operator has to be able to tell them apart from the recorded sentence alone.

## Where a refusal shows up

A refusal that is invisible is the silent freeze this gate must never become, so a `Red` lands in
three places at once:

1. **`ComboVerifications`** — the full verdict, per candidate tag, which the **Updates** settings tab
   already renders as *"cannot update to X — these modules do not compile or test against it"*,
   listing every failing module and every caveat.
2. **`HeldTag` / `HeldReason` / `HeldIndeterminate`** — the same hold fields the availability gate
   writes, so the existing surfaces need no new state. `HeldIndeterminate` is **false**: the gate
   looked and found an incompatibility, which is a candidate to re-verify, not an availability
   incident to fix.
3. **`PlatformUpdateStatus`** — `Derive` reads the recorded verdict as well as `IsHeld`, so the
   About page and the header build chip render `UpdateHeld` rather than an eternal "update
   available". Reading only `IsHeld` would have covered the empty state alone: the hold field is the
   poller's *note* about the refusal, while the verdict is the *fact*, and a tick whose hold write
   failed would otherwise have rendered a blocked build as available forever.

Only `Red` reads as held. A `NotVerifiable` is not a hold — nothing refused that build — and
rendering it as one would send an operator to fix an incompatibility that was never diagnosed.

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

The gate was therefore recorded, rendered on the tab, and **unable to refuse anything** — #2274's
"built, documented, tested, and called by nothing" with one extra step. Nothing de-duplicates the
content now. The trigger stream keeps its own `DistinctUntilChanged(content => content.Policy)`,
so a content change that is not a policy change still triggers nothing — including the poller's
own `LastCheckedAt`/`LastCheckVerdict` bookkeeping writes, which is why letting them through
cannot loop.

Pinned by `ComboGateRollTest.AVerdictRecordedAfterTheFirstCheck_RefusesTheNextRoll`, whose second
check is driven by the **safety net** on purpose: a policy change would refresh the content even
with the defect present, because it moves the very field the filter keyed on.

## Cost, and why the walk is cheap

`VersionSelect.PickTargets` returns every eligible tag newest-first and the poller takes the first
one that is rollable, so a not-yet-baked head never freezes the releases behind it. Asking a
*producer* per candidate would mean one full docker run per tag, so the walk uses only
`ComboVerificationGate.Recorded` — a pure read of the policy content already in hand. A verdict is
produced at most once per check, about the candidate actually chosen.

If that production comes back `Red`, the tick holds; the now-recorded `Red` makes the very next walk
step past that candidate. The two halves converge in one extra check instead of paying a gate run
per tag.

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

All three go in the **Actions** store of `Systemorph/MeshWeaver` and **only** there:
`combo-verify.yml` fires on `workflow_run` and `workflow_dispatch` and **never** on
`pull_request`, so its `secrets.` never resolve against the Dependabot store (there is no Dependabot
*variables* store at all). The **three** `AZURE_*` secrets (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`) and the two `FLEET_READER_*` secrets the same preflight asserts are
already provisioned for `main-cd`, and the roster is no longer an input at all — it is derived (the
next section). So five of the preflight's eight inputs are already in place, and the three below
are the whole of what is missing.

| Name | Kind | Value | Where it comes from |
|---|---|---|---|
| `COMBO_VERIFY_SOURCES` | variable | space-separated `name=url`, e.g. `plugins=https://github.com/Systemorph/MeshWeaver.Plugins` | the module source repositories the verifier materialises. 🚨 **Not derivable from the overlays, and that is a property of the data rather than of the effort spent**: it maps a registry *source NAME* carried by an install record to the repository that source's modules come from, and only the registry holds that mapping (`ComboAssembly.SourceRepositories`, consumed at `InstanceComboAssembler.cs:310`). It is plain data, not a credential. |
| `COMBO_VERIFY_KEYS` | secret | `{"<instance>":"mwi_…"}`, one per **derived** instance | 🚨 **ISSUED, never recovered.** An `mwi_` instance-registry key is stored hash-only (`InstanceKeys` persists `Hash(raw)`), so an existing key cannot be read back — a NEW key is issued per instance, additively, and separately revocable. |
| `COMBO_VERIFY_TOKENS` | secret | `{"<instance>":"mw_…"}`, one per **derived** instance | an API token of a **global admin** on that instance. #3891 made this removable — see below — but the lander still uses it, so it is required until that switch lands. |

🚨 **"One per instance" is now answered by the lane, not by the reader.** The preflight prints the
derived roster before it asks for credentials, and names the instance any map is missing
(`combo-verify.yml:194-198`). The hand-written value this page used to carry named `memex` and
`memex-cloud` — and the fleet's overlays declared more than that on the day it was specified, which
is the failure mode a derivation removes rather than a tidiness argument.

🚨 **And what the derivation says TODAY is a refusal, which is the mechanism working.** Measured
2026-09-15 over all three deployments repositories, the fleet declares **four live** installations:
`build` (build.meshweaver.cloud), `memex-cloud` (memex.meshweaver.cloud) and `memex`
(memex.systemorph.com) in `Systemorph/Memex`, **and a second `memex`** (partnerre.meshweaver.cloud)
in `Systemorph/PartnerRe.Memex` — with `pearl` and `partnerre` excluded by their `not-installed`
declarations in `.github/acr-retention/instances.json`.

Upstream that duplicate is **legal and correct**: since [#3438](https://github.com/Systemorph/MeshWeaver/issues/3438)
(2026-09-15) an installation's identity is `gh_repo:id`, because a `Hosting__Deployment` is unique
inside one deployments repository and inside nothing larger, and AXIS 3 only ever asks each one what
it is running. **Here it is fatal**, because `COMBO_VERIFY_KEYS` and `COMBO_VERIFY_TOKENS` are keyed
by NAME: two installations sharing one would be handed the same `mwi_` key and admin token, and the
second's verdict would land on the FIRST's `Admin/UpdatePolicy`. So the derivation REFUSES, naming
both declaring overlays, rather than emitting two rows called `memex`. Silently qualifying the name
to `repo:id` would be worse — it would ask for credentials under a key nobody has provisioned.

Resolving it is a decision, not a workaround: rename one installation, or key the maps by the
qualified `repo:id` and record that here. Until then the lane is red on the roster rather than on
the credentials, and it says which two overlays collide.

The names are asserted at `combo-verify.yml:99-129` (the inputs) and `:169-205` (the per-instance
half, which cannot run before the derivation); each `missing+=` line already names what to provision.
The `verdict` job at `:283-350` separates *no candidate* from *the preflight failed* from
*verification did not succeed*, so a red here reads as "verification never ran, provision X" rather
than as "verification failed". **That half of the lane is not the defect.**

### What being UNVERIFIED costs today: nothing gates on it

Two independent reasons, and both were measured rather than assumed:

1. **By design, an absent verdict neither clears nor refuses.** `ComboClearanceKind.NotVerified`
   "refuses nothing, because refusing on it would freeze every instance in the fleet on the day this
   gate shipped" (`ComboClearance.cs:20-30`). Only a `Refused` — a recorded **Red** — removes a
   candidate from the walk (`SelfUpdateHostedService.cs:891`) or holds a roll (`:1014`). An
   unverified roll is **applied**, with `LogWarning("[SelfUpdate] rolled {Tag} WITHOUT combo
   clearance")` and the verdict marked `Unverified` (`:1048-1056`). Of the other two readers, only
   `PlatformUpdateStatus.cs:105` treats a verdict as blocking, and only a Red;
   `UpdatePolicySettingsTab.cs:249-272` **renders all three kinds** — Green, Red and
   NotVerifiable each get their own rendering, with the verdict's caveats surfaced on every one —
   so nothing is hidden from an operator, there is simply nothing recorded to show.
2. **The one consumer is switched off on both live instances.** `Admin/UpdatePolicy` on
   memex.systemorph.com and on memex.meshweaver.cloud both read
   `lastCheckVerdict: "updates are disabled on this install (Admin/UpdatePolicy = None); the
   registry was not listed."` So the self-update poller — the only thing in this repository that
   consults a verdict at decision time — is not deciding anything to begin with.

That is **not** an argument for leaving it unprovisioned: the gate exists so that the day
self-update is switched back on, a candidate a live instance cannot serve is refused rather than
rolled. It is an argument about *priority* — this is a latent gate, not a live incident, and it
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
tree and mints that App's token (`combo-verify.yml:131-144`), which is the one structural change the
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

Measured: the `pearl` entry read *"never installed — pearl.meshweaver.cloud has no DNS record
(measured 2026-09-12)"* and ended *"Delete this entry the day it is provisioned."* pearl was
provisioned 2026-09-16 and answered `/api/version` from then on. Five days later the line still
stood, so pearl was outside this lane's roster **and** outside the nightly lock's AXIS 3 protected
set, and no run of either was red about it. The entry even named its own expiry condition; nothing
evaluated it.

**A non-live declaration is now re-measured against the one question that can falsify it — does a
portal answer at that host?** `build_instances` probes an exempted installation through the same
probe seam it already takes, and an ANSWER is a blocker naming the line to delete. The reading is
the returned **commit**, never the absence of an error, so `derive-combo-instances.py` — which
passes a probe that answers nothing — stays free of an HTTPS call to every portal in the fleet and
the arm is inert there by construction: one lane does the network. An exempted installation whose
overlay names no host (`partnerre`, whose template still reads `host: "TODO"`) cannot be falsified
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
`derive-combo-instances.py --self-test` drive all of them on every pull request.

### 🚨 MEASURED: the three inputs are necessary and NOT sufficient — the roster refuses first

This section used to open *"provisioning the three remaining `COMBO_*` inputs makes this lane green
today"*. **That is false, and it was never observed to be true**: the preflight asserts the inputs
BEFORE it derives the roster, so the derivation has never once run in CI — every run of this lane
has died one step earlier. Run by hand against the credential CI uses, it refuses:

```
::error::the combo-verification roster could not be derived:
  • two live installations are both named `memex` (Systemorph/Memex
    deployments/aks/memex/values.memex.public.yaml and Systemorph/PartnerRe.Memex
    deployments/aks/memex/values.memex.yaml). … this lane's credential maps
    (COMBO_VERIFY_KEYS, COMBO_VERIFY_TOKENS) are keyed by NAME: both would be handed the
    same instance key and admin token, and one's verdict would land on the other's
    Admin/UpdatePolicy. Rename one installation, or key the maps by the qualified repo:id
    and say so here.
```

The refusal is **correct** — it is the duplicate-host harm arriving through the other door — and it
became reachable when the fleet gained a second deployments repository: `Systemorph/PartnerRe.Memex`
carries `deployments/aks/memex/values.memex.yaml` declaring `Hosting__Deployment: "memex"` at
`partnerre.meshweaver.cloud`, alongside this fleet's own `memex`. The nightly lock sees the same
installation and names it `Systemorph/PartnerRe.Memex:memex`, so the collision is real under CI's
own credential, not an artefact of a narrow scan.

**So the remainder is provisioning PLUS one decision**, and the decision is cheapest now, while the
maps do not exist:

- **Renaming** one installation changes what it reports as (`Hosting__Deployment` is its inventory
  identity), and the one that would have to move is in PartnerRe's estate.
- **Qualifying** the maps by `repo:id` removes the collision but then DEMANDS a credential for a
  portal in another party's subscription — one this fleet will never hold — so the lane stays red,
  differently worded.
- **Scoping the denominator** is the answer the question actually wants: combo verification asks
  *may THIS candidate image roll to THAT instance*, which is meaningless for an installation that
  never receives our images. PartnerRe's `memex` runs its own build (core `293bfff` on
  2026-09-21) from its own ACR. Expressing that needs the `registries` table to account for
  PartnerRe's current ACR, which it does not — it names `memexaksacrqoqqdqnhlaksg.azurecr.io`
  while the live overlay pins `memexaksacr43rzd6faaix36.azurecr.io`, and that staleness is what
  has held `lock-pinned-digests` red since 2026-09-15. Declaring another party's registry is not a
  statement this repository can verify on its own.

Whichever is chosen, **it is not optional and it is not the operator's** — provisioning the three
inputs against today's tree buys a different red, not a verdict.

### The three inputs are still the whole of the CREDENTIAL prerequisite — `verify:combo` is not one

The lander does not use the
`verify:combo` route at all: `combo-verify-instance.sh` reads `roll-target` and `combo` with the
`mwi_` instance key and then lands the verdict by `POST /api/mesh/get` + `POST /api/mesh/patch`
with `ADMIN_TOKEN` (steps 1–4 of that script). `/api/plugins/combo-verification` and its
`verify:combo` grant are the *destination* of the planned migration off that admin token, not a
precondition for the lane running.

Stating it the other way round — as an earlier revision of this page did — hands an operator a
prerequisite that does not exist and blocks the remediation that would actually work.

So the remainder, in the order it can be done:

0. **Resolve the duplicate `memex`** (the section above). Not the operator's, and not skippable:
   until it is resolved the derivation refuses and no instance is verified whatever is provisioned.
1. **Provision the three inputs** (the table above). Nothing in this repository can substitute for
   it: a credential is issued at the service that holds it, not derived. Two of the three cannot be
   copied from anywhere — an `mwi_` key is hash-only and an `mw_` token is issued once — so they are
   NEW credentials, additive and separately revocable, verified against the live instance before
   they are written.
2. ~~**Enumerate instances from the deployment overlays**~~ — **done** (#3848). The roster is
   derived; `vars.COMBO_VERIFY_INSTANCES` is gone from the preflight and from this page.
3. **Grant `verify:combo` to the build identity on each instance**, then **switch the lander off
   the admin token** — dropping `COMBO_VERIFY_TOKENS` from the preflight and the job env in the
   same diff, since an input asserted but no longer consumed is the no-skip-trapdoor rule in
   reverse. The grant is portal data an operator provisions per instance, and it must land
   *before* the switch: `POST /api/plugins/combo-verification` requires
   `outcome.Build?.Allows(BuildVerbs.Verify, "combo")` (`ReleaseGateEndpoints.cs:291, :302`), which
   an `mwi_` instance key does **not** satisfy. This step reduces the per-instance credentials from
   two to one; it does not gate the lane.

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
