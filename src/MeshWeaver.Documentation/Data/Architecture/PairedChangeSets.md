---
Name: Paired Change Sets
Category: Architecture
Description: How an author pairs a core pull request with a MeshWeaver.Plugins pull request — core merges first on its own checks and waits for no other repository; the Plugins half is verified against the sealed core set that contains the core half.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="7" cy="12" r="3"/><circle cx="17" cy="12" r="3"/><line x1="10" y1="12" x2="14" y2="12"/></svg>
---

# Paired Change Sets

**A change that spans core and MeshWeaver.Plugins merges per repository, core first, and core waits
for nothing on the Plugins side** (policy `dependents-verify-on-their-side`, which superseded
`dependent-suites-affected-gate`; register: [Policy Not Prose](../PolicyNotProse)). Core runs no
Plugins suites and reads no Plugins verdict; there is no `Breaks-plugins:` declaration any more.
The pair is verified on the PLUGINS side, against the sealed core set that contains the core half.
The deploy side does not hold an unverified combination back: arming ([One Promotion
Gate](../OnePromotionGate)) judges platform evidence only, and the combo verdict is advisory
([Combo Gate Wiring](../ComboGateWiring)).

## How to pair two pull requests

1. **Core pull request body:** `Pairs-with: Systemorph/MeshWeaver.Plugins#<n>` — the same line the
   [cross-repo pair gate](../CrossRepoPairGate) reads.
2. **Plugins pull request body:** `Core-ref: Systemorph/MeshWeaver#<n>`.

That is all. What happens:

| where | what runs | what it proves |
|---|---|---|
| core PR — `Cross-repo pair (public surface)` | core's own gate, reading the `Pairs-with:` line | a core half that REMOVES public surface lands only after its counterpart (deleting half last) |
| Plugins PR — `Resolve the released platform` | the normal Plugins CI against the newest sealed core set | once the core half has landed AND is in a sealed set, the Plugins half is tested against it like any other change |
| Plugins `main` — push and daily runs | Plugins' own suites against each new sealed core set | a core change that breaks Plugins is seen THERE, as Plugins' red, and fixed by a counterpart pull request |

## The order

1. **Core merges first**, on its own checks — nothing on the core side waits for Plugins.
2. core's CD builds and promotes the landed commit (~25 minutes to a promoted set).
3. **The Plugins half re-runs** — push, or re-run the workflow. `Resolve the released platform` checks
   that the resolved set CONTAINS the declared core pull request's merge commit (by ancestry) and is
   RED, naming the set it saw, while it does not — so the Plugins half never goes green against a
   core that lacks what it needs.
4. The Plugins half merges; the next main-cd build pairs both, and the promotion gate arms the set
   on its platform verdict.

**Deleting-half-last still holds.** When the core half REMOVES public surface the Plugins half uses,
the Plugins half must land first and be compatible with both — then it declares NO `Core-ref:` (it
does not need the core change), and the core half declares `Pairs-with:` as the pair gate demands.

## Not covered

- Every satellite — MeshWeaver.Plugins included — finds a core break in its own push and daily runs
  against the sealed core set; nothing measures an UNMERGED core candidate against a dependent.
- In-mesh NodeType source is compiled by the compile gates, not here.
