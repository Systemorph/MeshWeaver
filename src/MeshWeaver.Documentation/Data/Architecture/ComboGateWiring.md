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
a build that passed every CI job, has a sealed content bake, satisfies every module floor, and still
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
- [Guards and Unknown States](/Doc/Architecture/GuardsAndUnknownStates) — the general form of "an
  unanswered check is not a passing one".
