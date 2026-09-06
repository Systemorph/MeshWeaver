---
Name: Mesh Admission
Category: Architecture
Description: Readiness gates traffic; admission gates membership. Where a process actually joins the mesh, what a provisional membership that cannot stamp a generation looks like, and why a refused process stays up inert instead of exiting.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 10h18"/><path d="M9 15h6"/><circle cx="12" cy="15" r="0.5"/></svg>
---

> **"not ready should *NEVER* run" · "validate before running"** — maintainer, 2026-09-06

A readiness probe takes a pod out of the load balancer. It does **not** take the process out of the
mesh. On 2026-09-06 that difference cost `memex.systemorph.com` a two-hour client-facing outage
([#3478](https://github.com/Systemorph/MeshWeaver/issues/3478), [#3472](https://github.com/Systemorph/MeshWeaver/issues/3472)) —
and it did so through a protection that was working *correctly*.

## What happened

A roll to `3.0.0-ci.7926` started at 19:17Z. The NodeType bake readiness gate measured a regression
and refused readiness:

```
Health check nodetype_bake with status Unhealthy:
  'NodeType bake regressed on this image — refusing readiness so the rollout stalls with the
   previous image still serving. 1 NodeType(s) regressed on this image: Feedback/Feedback'
```

That verdict was right, and the rollout correctly stalled. Two hours later:

| pod | image | ready | started |
|---|---|---|---|
| `79ddd44d77-k8qlc` | `rc9.ci.7693` | true,true,true | 18:54:57Z |
| `79ddd44d77-xzp5d` | `rc9.ci.7693` | true,true,true | 18:57:30Z |
| `f69cd9d56-jczb7`  | `ci.7926`     | **FALSE**,true,true | 19:17:02Z |

The refused pod kept running, kept its hubs activated, and **kept stamping NodeType compile
records**. `Crm/Offer` (19:28:04) and `Crm/Opportunity` (19:31:30) came to name assemblies stamped
`sc273ee39f…` while the serving replicas ran `s2f227642d…`; both types went unloadable and every
deal and offer page on the client portal was dead until 21:50Z. `compilationStatus` read `Ok`
throughout — the types compiled, just not for the identity that had to load them.

**The protection produced the outage**, not by misjudging the image but by leaving the refused
process a participant.

## Where a process actually joins

The first instinct — "do not join until validated" — does not survive contact with the ordering. A
portal process becomes *addressable* long before it can have validated anything, and the validation
itself needs the mesh: the bake sweep reads every NodeType and its sources through mesh queries. So
membership-for-**reading** cannot be moved after the verdict.

Membership-for-**writing** can, and that is the half that does the damage. The mesh-visible acts
that carry a process's own build identity are few and enumerable:

| act | where | what it says |
|---|---|---|
| NodeType compile stamp, batch path | `NodeTypeBatchBake.WriteStamp` | `CompiledFrameworkVersion`, `CompiledModulesHash`, `CompiledDependencies`, assembly coordinates |
| NodeType compile stamp, activation path | `NodeTypeCompilationHelpers` (post-compile write-back) | the same field-set — the two paths share it literally |
| module-set adoption | `ModuleSetStore.RecordAdoption` | "a replica is serving set N" |

**Assembly BYTES are deliberately not gated.** The assembly store is keyed
`(nodeTypePath, version)` with the producing framework identity baked into the key, and
`NodeTypeBakeStatus.ClassifyDetailed` cannot look for bytes at all until the RECORD names a
collection, a path and a version. Bytes nobody's record points at are inert. **The pointer is the
poison, so the pointer is what is gated.**

## The mechanism

`MeshPublicationGate` (`src/MeshWeaver.Mesh.Contract/Services/MeshPublicationGate.cs`) is a
mesh-scoped singleton every one of those writes goes through. It reads its verdict from the
registered `IMeshAdmissionAuthority` implementations — today exactly one, the NodeType bake gate —
and takes the most restrictive answer.

| admission | meaning | what a publication does |
|---|---|---|
| `Unarmed` | no authority is armed | runs, unchanged |
| `Provisional` | armed, no verdict yet | **held**, released on admission |
| `Admitted` | validation passed | runs |
| `Refused` | validation failed | **never built, never written** |

Publications are offered as a `Func<IObservable<Unit>>`, not as an observable: a refused publication
is never *constructed*, so no storage read is issued and no `RequireSubscribeObservable` is minted
only to be dropped. A held one is constructed at release time, so its compare-and-set reads the row
it is about to race rather than replaying a version read before the verdict existed.

### Provisional membership, stated

> A provisional member may **read** the mesh and **compile**. It may not **publish**. Everything it
> would publish is held, in offer order, and settled the instant a verdict exists.

That is what makes *validate before running* implementable without reordering startup. The bake
sweep runs exactly as before; what changed is that its 240 stamps land **after** its verdict instead
of during it.

### The second witness — why gating the compile stamp does not break self-healing

The bake gate already retracts a regression when the type it condemned is afterwards seen to build
on this image (#1214 — a bake that compiled a half-applied plugin update recorded four false
regressions and hung a rollout until the content converged seconds later). That watch read the
type's **shared record**, because that is where the compile watcher publishes.

🚨 **Gating publication breaks that proxy, and leaving it broken would trade one outage class for
another.** A refused pod recompiles the type successfully, the stamp is withheld — correctly, it
names an identity the serving replicas cannot load — and a record-only watch waits forever for a row
that will never move. #1214's *self-healing* stall becomes a *permanent* one.

So the retraction now has two witnesses, and takes whichever answers first:

- **`LocalNodeTypeBuilds`** — a mesh-scoped signal the compile write-back raises *before* the
  (possibly withheld) stamp. It is strictly better evidence than the record: no round-trip, no
  framework comparison, no freshness heuristic. The question was always process-local; the record
  was the proxy.
- **the record**, unchanged — a type baked by a *peer* on the same image is a real recovery the
  local signal cannot see, and dropping it would narrow the retraction rather than widen it.

## One predicate, two consumers

The defect #3478 names is a **divergence**: two verdicts, one of them enforced. So admission is not
a second gate with its own table — it is derived from the readiness predicate:

```csharp
public bool ReadinessGranted => Phase switch
{
    BakePhase.NotStarted => true,       // the sweep is switched OFF — the fail-OPEN state
    BakePhase.Complete   => true,
    BakePhase.Faulted    => AllowUnprovenBake,
    _                    => false,      // Running, Regressed
};

public MeshAdmission Admission
    => !GatesReadiness ? MeshAdmission.Unarmed
        : ReadinessGranted ? MeshAdmission.Admitted
        : Phase is BakePhase.Running or BakePhase.NotStarted ? MeshAdmission.Provisional
        : MeshAdmission.Refused;
```

**The invariant is `Admitted ⟹ ReadinessGranted`** — admission is never more permissive than
readiness, which is the maintainer directive reduced to one line a test can falsify over the whole
state space.

It is deliberately *less* permissive in exactly one place: an **armed gate at `NotStarted`**.
Readiness is granted there (a probe cannot tell "switched off" from "has not started"), but no
verdict exists, so publications are held rather than run. The incident's pod walked through exactly
that window — its bake starts at `ApplicationStarted`, minutes after the process does.

🚨 **Unarmed means unarmed.** With `PreWarm:GateReadiness` off — the chart default, every dev host
and every test mesh — nothing consumes the bake verdict and nothing is enforced from it. Making an
unarmed gate withhold writes would be enforcement nobody opted into, and would turn a configuration
default into an outage. "Registered" and "armed" stay separate, exactly as they already do for the
readiness half.

## Becoming unhealthy after joining

The verdict is **level-triggered**, re-read at every publication rather than latched at admission.
A regression recorded hours after a clean sweep stops the process publishing from that moment; a
`RetractRegression` (the type has since built on this image) resumes it. That answers the symmetric
question with the same mechanism and no second one.

Note what it does *not* do: it never withdraws a pod that is serving correctly. Withholding
publication is not the same act as flipping readiness — the pod keeps answering requests from the
build it already has, it simply stops telling the mesh about new ones.

## Exit, or stay up inert? — the decision, and its trade-off

A refused process **stays up and inert**. It does not exit.

**The case for exiting** is real: a non-zero exit turns a stalled rollout into a `CrashLoopBackOff`,
which is louder, harder to overlook, and arguably a more honest description of a process that will
never serve.

**Why inert is what ships:**

- **The refusal is already real without it.** Once publication is gated, exiting adds no protection
  — it only changes how the stall is *presented*. Loudness is not the defect #3478 describes.
- **It destroys the diagnosis surface.** The refused pod's `/health` payload naming the regressed
  types is the only place that verdict lives. A crash-looping pod has no readable health endpoint,
  and its logs rotate with each restart.
- **Each restart re-runs a full cold bake** — measured at ~2.4 s per NodeType, ~10 minutes on the
  largest mesh we run. A backoff loop pays that repeatedly, on a cluster we pay for, to learn the
  same verdict every time.
- **It changes deployment behaviour on every path**, including the Monolith, Aspire and the plugin
  tester, on evidence this change is the first to produce. `ModuleSetConvergence` declined the
  neighbouring options ("have a behind replica restart itself, or flip readiness") for the same
  reason, and noted they become *decidable* once the state is named. This is that state, now named.

It is a **maintainer-level call and it is decidable now**: with admission derived from one
predicate, "exit when `Admission is Refused`" is a single call in
`DynamicTypePreWarmerHostedService`'s terminal handler. It is deliberately not taken here.

## What this does not fix

- **It does not stop a bad image being CHOSEN.** That is roll selection —
  [#3479](https://github.com/Systemorph/MeshWeaver/issues/3479) — and the two are complementary:
  that one stops the bad roll starting, this one stops a bad roll doing damage when one happens
  anyway. Neither subsumes the other.
- **It does not gate Orleans cluster membership or hub activation.** A refused process is still
  addressable and its grains still activate. That is what lets the sweep read; it is also the
  remaining, deliberate, scope boundary.
- **It does not change the stall posture.** The old image keeps serving. That instinct was right;
  what was missing was the refusal being real.

## Related

[Module Set Convergence](/Doc/Architecture/ModuleSetConvergence) ·
[NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) ·
[Modules](/Doc/Architecture/Modules) ·
[Deployment (AKS)](/Doc/Architecture/DeploymentAKS) ·
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals)
