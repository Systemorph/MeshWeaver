---
Name: Paired Change Sets
Category: Architecture
Description: How an author pairs a core pull request with a MeshWeaver.Plugins pull request so the two are built and tested TOGETHER before either merges — and why merging stays per repository with core first and never waiting.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="7" cy="12" r="3"/><circle cx="17" cy="12" r="3"/><line x1="10" y1="12" x2="14" y2="12"/></svg>
---

# Paired Change Sets

**A change that spans core and MeshWeaver.Plugins is tested as ONE change set before either half
merges.** Merging stays per repository, core first, and core never waits for Plugins (policy
`core-merge-never-blocked`, [One Promotion Gate](../OnePromotionGate)).

## How to pair two pull requests

1. **Core pull request body:** `Pairs-with: Systemorph/MeshWeaver.Plugins#<n>` — the same line the
   [cross-repo pair gate](../CrossRepoPairGate) reads.
2. **Plugins pull request body:** `Core-ref: Systemorph/MeshWeaver#<n>`.

That is all. What happens:

| where | what runs | what it proves |
|---|---|---|
| core PR — `Dependent suites (request)` / `(MeshWeaver.Plugins, advisory)` | MeshWeaver.Plugins `core-candidate.yml`, against the core PR's merge commit AND the Plugins PR's head | the two halves compile and the reachable Plugins suites pass TOGETHER — advisory, reported on the core PR |
| Plugins PR — `paired-core.yml` | the same `core-candidate.yml`, dispatched from the Plugins side while the core PR is open | the same pairing, re-run on every push to either half's Plugins side |
| Plugins PR — `Resolve the released platform` | the normal Plugins CI against the newest green core `main` build | once the core half has landed AND is in a green build, the Plugins half is tested against it like any other change |

## The order

1. **Core merges first**, on its own checks. The paired run is information for the author, never a
   gate on the merge.
2. core's CD builds and promotes the landed commit (~25 minutes to a promoted set).
3. **The Plugins half re-runs** — push, or re-run the workflow. `Resolve the released platform` checks
   that the resolved set CONTAINS the declared core pull request's merge commit (by ancestry) and is
   RED, naming the set it saw, while it does not — so the Plugins half never goes green against a
   core that lacks what it needs.
4. The Plugins half merges; the next main-cd build pairs both, and the promotion gate arms it once
   the dependent suites pass.

**Deleting-half-last still holds.** When the core half REMOVES public surface the Plugins half uses,
the Plugins half must land first and be compatible with both — then it declares NO `Core-ref:` (it
does not need the core change), and the core half declares `Pairs-with:` as the pair gate demands.

## Not covered

- Satellites other than MeshWeaver.Plugins are not paired this way; they find a core break in their
  own daily run.
- The paired run builds Plugins suites from source against the core candidate; in-mesh NodeType
  source is compiled by the compile gates, not here.
