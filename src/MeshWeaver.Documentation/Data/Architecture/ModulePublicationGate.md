---
Name: The Module Publication Gate
Category: Architecture
Description: A module bundle used to reach the live registry from inside its own pack leg, before the run's sibling suites, the portal-host shards, the NodeType compile-check and the Tests-area gate had reported. Why the fix is a MOVE and not a re-ordering, what the downstream publication lane refuses, and which halves of it a runtime verdict cannot see.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6"/><path d="m9 5 3-3 3 3"/><rect x="3" y="8" width="18" height="6" rx="1"/><path d="M7 14v3a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2v-3"/><circle cx="12" cy="20" r="2"/></svg>
---

# The Module Publication Gate

**A module bundle becomes visible to every installation the moment it is handed to the registry.
So the hand-over may not happen until the run that produced it has finished deciding whether the
source was any good.**

Until MeshWeaver#3878 it did. `node-repo-module-pack.yml` POSTed each bundle from INSIDE its own
matrix leg, immediately after that module's own suite — and everything else that validates the
source runs beside or after that leg.

## What the ordering actually was

```
 t=0                                                                              t=39 min
 │ select → prepare → build-workspace                                                    │
 │        ├─ pack (MeshWeaver.AI)     build → upload → tests → POST to the registry ◀── here
 │        ├─ pack (MeshWeaver.Mcp)    build → upload → tests → POST                      │
 │        └─ …                                                                           │
 │ compile-check (every NodeType)          ────────────────────────────────▶ verdict     │
 │ test-repos (the Tests-area gate)        ────────────────────────────────▶ verdict     │
 │ portal-hosts (shards)                   ────────────────────────────────▶ verdict     │
```

The POST marked `◀── here` lands while three of the four validation lanes have not reported. A red
twenty minutes later changes nothing about what portals have already adopted: a registry serves what
it holds, and `ModuleUpdateDecision` on every installation reconciles against exactly that.

## Why it is a MOVE, not a re-ordering

The obvious repair — make `pack` depend on the gates — is not available. `modules-floor`'s bundle
artifacts are the INPUT to `compile-check` and to the Tests-area gate, so the edge would close a
cycle. Everything a downstream gate consumes has to keep existing at the moment it exists today.

So only the POST moves:

| | before | after |
|---|---|---|
| build, bundle upload, built marker | in `pack` | **unchanged, same point in the run** |
| the module's own suite | in `pack` (publishing runs) | **unchanged** |
| `bundles-built`, receipts, the identity checks | in `pack` / `verify` | **unchanged** |
| the POST to the registry | in `pack` | in `node-repo-module-publish.yml`, downstream of everything |

`publish-mode: staged` on the pack call is the switch. The leg writes down what it WOULD have
published — the bytes, plus a record naming them by sha256 and stating the framework identity read
back OFF those bytes — as `module-publication-<lane>-<module>`. The caller then runs the publication
lane in a job wired to its whole validation set. `publish-mode: direct` is the default and is the
behaviour every unconverted caller keeps.

## What the publication lane refuses

The house rule this sits next to is the one about gates: *"it did not run" and "it passed" must
never be indistinguishable*. A hand-over that completes before its inputs are validated is the same
defect class. So nothing in this lane asks whether something happens to be set, and no absent
answer resolves to a positive one.

### 1 · The verdict

`verdicts: ${{ toJSON(needs) }}` — the calling job's whole needs context, verbatim. Every entry must
carry `result: success`. Each of these is a refusal that NAMES the job and what it actually said:

| what the dependency said | why it is refused |
|---|---|
| `failure` | the source validation reached a negative verdict |
| `cancelled` | it reached none at all |
| `skipped` | **the trap this exists for** — GitHub paints a skipped job with the same tick as a passed one |
| absent from the context | a job named in `required-jobs` that is not in `needs:` never reported |
| present with no readable `result` | a verdict this lane cannot read must never resolve to "it passed" |
| an EMPTY needs context | a publication that waited for nothing is the defect, not the base case |
| an EMPTY `required-jobs` | "every required job passed" would be vacuously true |

### 2 · The caller's graph

The verdict cannot see one mutation: a dependency removed from `needs:` **and** from
`required-jobs` is invisible at run time — there is nothing left to be missing. So the lane checks
out the caller's own repository at the commit being published, finds the job that calls it (via
`github.workflow_ref`) and asserts its shape:

* every `required-jobs` entry exists and is a transitive `needs:` ancestor;
* the `if:` carries **no status function** — `always()`, `!cancelled()`, `success() || failure()`
  each re-open the door the verdict closes, and a publishing job runs under GitHub's default
  all-needs-succeeded rule or not at all. Gating the EVENT (trunk-only) is what an `if:` is for;
* `verdicts` is the literal `${{ toJSON(needs) }}` — a curated object literal would let the caller
  answer the verdict question with a value it chose;
* no `continue-on-error`;
* **every other job in the caller's workflow** is either upstream of the publication, downstream of
  it, or named in `unrelated-jobs` with a reason. An omission is red and names the job, so "we
  forgot it" and "it does not matter" cannot look the same. A `unrelated-jobs` entry the workflow no
  longer has is red too — a stale exemption hides the job that replaced it.

### 3 · The staged evidence

Validated in full BEFORE one byte is POSTed, so a refusal leaves the registry untouched rather than
half-served:

* exactly one publication record per module, stamped with **this call's lane key**. Artifacts are
  run-wide and a repo may call the pack lane twice in one run (Plugins: floor + rest), so a
  foreign-lane record is set aside and named — the same failure `bundles-built` was fixed for;
* every module in the pack call's own `selected` output has a record. A selected module with no
  record is red: *its leg never staged* must not read like *there was nothing to stage*;
* a record that owes no hand-over (the build ledger already serves the key) says so, with the
  reason — so "not owed" and "never staged" stay distinguishable;
* the bytes are re-hashed and compared to the sha256 on the record; the manifest's `frameworkMvid`
  is read back out of the zip and compared to the identity the record states; the manifest's
  `module.assemblyName` must be the module. Substituted, truncated or identity-less bytes refuse;
* an empty publish token FAILS the job rather than publishing nothing quietly.

### 4 · The ledger

A staged artifact is **not** a `Published` ledger record. The `Published` transition is written only
after the registry answers 2xx, and only by the lane that got that answer — so a run that stages and
then fails validation leaves a key a later run will republish, rather than one the fleet believes is
already served. Reuse and coalescing are otherwise untouched: see
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture).

## How the gate is proven

`node-repo-module-publish.yml` is a `workflow_call` lane a satellite invokes; nothing on a core pull
request runs it, so the first execution of an edit to it would otherwise be a satellite's production
publication. Two things run in core CI instead, self-test first:

* `module-publication.py --self-test` — the refusals above, each shown to fire on its defect and
  stay silent on its fix, including the two mutations #3878 names: a required dependency removed
  from the caller's `needs:`, and a status function added to the publishing job's `if:`.
* `test-module-publication.py` — the harness EXTRACTS the lane's real steps by `id:` (never a copy;
  a step it cannot find fails the harness) and executes them against a stub registry that RECORDS
  every request. Both directions are asserted: a green validation set delivers the exact staged
  bytes to `/api/plugins/bundles/<package>?version=…&packagePath=…`, and a later sibling failure
  leaves the request log EMPTY even though the module packed and staged successfully. "The registry
  was left untouched" is read off the log, never off an exit code.

## What this does NOT solve

Stated because #3878 says it must not be described as solved by moving one step:

* **atomic visibility if the publication itself fails or is cancelled part-way** — the lane
  validates the whole set before it POSTs, but the POSTs are still per module;
* **every publisher's source provenance**, and **last-green source selection** — MeshWeaver#3842
  and the generation work in #3461;
* **core's own `plugins-bake`**, which publishes a NodeType and module set through a separate path.

Related: [The Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) for the other family of
"green here, red there" couplings, and [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) for
why a skipped required context counts as satisfied.
