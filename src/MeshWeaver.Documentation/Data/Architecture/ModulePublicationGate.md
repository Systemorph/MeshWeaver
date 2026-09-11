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

## The caller that adopted it: MeshWeaver.Plugins

`modules-floor` passes `publish-mode: staged`; everything it builds, uploads, marks and tests stays
exactly where it was, because `compile-check` and the Tests-area gate consume those artifacts. The
POST moved into a new job, `publish-modules`, which calls this lane:

| input | value | why |
|---|---|---|
| `if:` | `modules-floor`'s `publish:` condition, verbatim — push to `main`, `repository_dispatch`, `schedule` | a run that stages and does not publish hands the registry nothing while every tick is green |
| `needs:` / `required-jobs` | `platform-ref preflight validate validate-name-shim repo-gates compile-check test-repos tests-ratchet portal-hosts-gate modules-floor gates-executed` | the gate set `publish-bake` already honours, plus the jobs this call reads outputs from, the legacy-name shim of `validate` and the skip detector — one verdict for both publishers of the run |
| `unrelated-jobs` | `supersede`, `test-drift`, `rn-app`, `e2e-static`, `memex-template`, `tag-modules`, `publish-bake`, each with its reason | none of them validates bytes that reach a module bundle |
| `lane` / `selected` / `modules` | `needs.modules-floor.outputs.lane` / `.selected` / `.declared` | one pack call, one publication; `declared` is the matrix that call was given |
| `permissions` | `contents: read` | exactly what the lane's one job demands |
| `publish-token` | `secrets.REGISTRY_PUBLISH_TOKEN` | the SAME secret `modules-floor` used to hold — no new credential, scope or permission |

`declared` is a new output of `node-repo-module-pack.yml`: the `modules` input, verbatim. Without it
the publisher would need a second copy of the 37-entry catalog in the caller, which drifts — and
Plugins' `check-modules-published.py` refuses a second `modules:` list in its `ci.yml` outright
(#3732).

The token's `pr-secret-preflight-allow.txt` entry, which exempted it on `modules-floor`, is deleted
in the same change: `publish-modules`' `if:` is provably false on a pull request, so no
pull-request-reachable job references the token any more and the checker refuses a stale entry.

## Both directions, on the real caller

**Green validation publishes.** Every job in `needs:` succeeds, so GitHub runs `publish-modules`;
the verdict, the caller-graph check and the staged-evidence check pass; the exact staged bytes are
POSTed to `/api/plugins/bundles/<package>?version=…&packagePath=Plugins/<package>`; the ledger
records `Published` only after the 2xx (Plugins runs with the ledger off).

**A later sibling failure leaves the registry untouched**, even though every module packed and
staged successfully. How each non-positive verdict is refused:

| verdict | what stops the hand-over |
|---|---|
| `failure` | GitHub's default rule skips `publish-modules`, so the lane never runs; if a caller ever re-opened the `if:`, the lane's `verdict` step refuses by name |
| `cancelled` | the same default rule; the verdict step refuses `cancelled` too |
| `skipped` | the same default rule, which does NOT treat a skipped need as satisfied the way branch protection treats a skipped context; the verdict step refuses `skipped` by name |
| missing | a validation job absent from `needs:` is caught statically on the pull request (`check-callers`, below) and at run time (`caller-gate`); a name in `required-jobs` that is not in `needs:` is refused by the verdict step as "NOT among this job's needs" |
| unknown | a needs entry with no readable `result` is refused by the verdict step — it never resolves to "passed" |

The consequence has to be said out loud: **while Plugins `main` is red, no module version reaches
the registry** — including the release-follow republish that stops portals reading
`FrameworkDeclined` after a platform release (#2088). That is the point of the change (a red main
must be invisible to portals, #3842), and it means a red `main` now delays module adoption rather
than serving unvalidated bytes.

## Checked before merge: `check-callers`

The lane's `caller-gate` step only runs when the lane is invoked, which is on trunk — so the first
judgement of a broken publisher would be the post-merge run on `main`, refusing, with `main` red.
`node-repo-validate.yml` therefore fetches `module-publication.py` and runs
`check-callers --root .` on every pull request of every satellite. It runs the same caller-graph
check over every publisher in the repository's workflows, and adds the wiring one call cannot see:

| refused | why |
|---|---|
| a `publish-mode: staged` pack call with no publisher | the staged bytes reach nobody and the repository silently stops publishing |
| two publishers on one pack call | the same validated bytes would be POSTed twice |
| a publisher whose `if:` differs from the pack call's `publish:` | stage-without-publish is silent; publish-without-stage refuses |
| `lane`, `selected` or `modules` not read from the SAME pack call's outputs, or that call not a direct need | a sibling call's evidence could answer the publication, or the output does not resolve |
| a publisher wired to a `direct` pack call | that call POSTs in-leg and stages nothing |
| `required-jobs` / `unrelated-jobs` passed as an expression, or `required-jobs` empty | the static half could not read what the lane will enforce |

A `direct` pack call that publishes is **named, not refused** — refusing it would red every
repository that has not adopted this lane yet. A repository that calls neither lane prints so.

## Landing order across the two repositories

A new caller of a platform lane has to appear in core's fleet roster
(`.github/lane-caller-grants.yml`, see [Workflow Permission Pairing](/Doc/Architecture/WorkflowPermissionPairing)),
and every satellite asserts its roster row against core's `main`. Under strict equality that is
unlandable in either order, so the Plugins row landed first marked `pending:`, which excuses
absence only. The order is: core (the `declared` output, `check-callers` in the validate lane, the
pending row), then the Plugins caller, then a core follow-up that removes the spent marker.

## What this does NOT solve

Stated because #3878 says it must not be described as solved by moving one step:

* **atomic visibility if the publication itself fails or is cancelled part-way** — the lane
  validates the whole set before it POSTs, but the POSTs are still per module;
* **every publisher's source provenance**, and **last-green source selection** — MeshWeaver#3842
  and the generation work in #3461;
* **core's own `plugins-bake`**, which publishes a NodeType and module set through a separate path.
* **MeshWeaver.SocialMedia still publishes in-leg.** Its `module` job calls the pack lane with
  `publish:` set and no `publish-mode`, so each bundle still reaches the registry right after its
  own suite. `check-callers` names it on every SocialMedia pull request; adopting the lane there is
  a separate change.
* **The jobs Plugins declares unrelated do not gate the hand-over.** A red `rn-app`, `e2e-static`,
  `test-drift` or `memex-template` still publishes, exactly as it still bakes — they are not part of
  either publisher's verdict.

Related: [The Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) for the other family of
"green here, red there" couplings, and [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) for
why a skipped required context counts as satisfied.
