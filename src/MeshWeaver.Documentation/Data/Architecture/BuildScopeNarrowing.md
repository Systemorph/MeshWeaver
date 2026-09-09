---
Name: What a Pull Request Rebuilds
Category: Documentation
Description: How node-repo-scope.py decides which module bundles a pull request rebuilds and which module suites it runs — the two blind spots that made a one-file documentation diff cost 105 jobs and 345 job-minutes, and why a divergence now costs the divergent directory rather than the whole run.
---

# What a Pull Request Rebuilds

`.github/scripts/node-repo-scope.py` decides, for every node repository's `Module bundles` lane,
**which module bundles a pull request rebuilds and which module suites it runs.** It is the one
place in the fleet whose job is to say *no*, so it is written biased: every uncertainty resolves
to a FULL run, out loud, naming which uncertainty it was.

That bias is right and this page does not weaken it. What it documents is two places where the
script said "I cannot tell" about a file it *could* have classified, and one place where it
answered a disagreement by switching narrowing off entirely.

> Where the narrowing sits in the wider lane: [CI Content Bake](/Doc/Architecture/CiContentBake) §
> "Rebuild only what a change AFFECTS". What a green run does and does not prove:
> [Reading CI Signals](/Doc/Architecture/ReadingCiSignals).

## The classifier

Every changed path lands in exactly one bucket:

| the path looks like | bucket |
|---|---|
| `<node package>/…` (a top-level dir with an `index.json`) | the CONTENT half — the caller's `affected-modules.py` answers the closure |
| `src/<project>/…` | the COMPILED half — the caller's `project-closure.py` answers the closure |
| a top-level dir in **`NOOP_DIRS`** | reaches nothing this lane builds |
| a repo-root file in **`NOOP_FILES`** | reaches nothing any lane builds |
| **anything else** | EVERYTHING — the full set, naming the file |

The last row is the safety net, and every entry above it is a claim that a class of path cannot
change what a module builds.

## Blind spot 1 — a repo-root file has no top-level directory

`NOOP_DIRS` is matched as `"/" in f and top in NOOP_DIRS`. **A single-segment path takes neither
no-op branch**, so before 2026-09-08 every repo-root file fell through to EVERYTHING.

Measured on MeshWeaver.Plugins run 34158493654 — a pull request whose entire diff was one file:

```text
AGENTS.md  → EVERYTHING (tooling / repo-root / unknown scope)
scope: full — 1 changed file(s) reach the tooling/global scope — first: AGENTS.md.
34 of 34 selected module(s) still owe a test run: MeshWeaver.Graph.Views, …
```

**105 jobs. ~345 billable job-minutes. ~54 minutes of wall clock. For a documentation edit** — and
`AGENTS.md` is the file every agent session in this fleet edits. The `docs/` entry in `NOOP_DIRS`
was added for exactly this shape and could not cover it, because the fleet keeps its agent guidance
at the repository root rather than under `docs/`.

`NOOP_FILES` closes it, and **it is a closed list of names, never a pattern.** That is the whole
safety argument: a root file the list does not name is still UNKNOWN and still runs the full set.
Measured across the fleet, the root files that must stay on the loud path are `Directory.Build.props`,
`global.json`, and every satellite's gate allow files — `plugin-gate.allow`, `plugin-tests.allow`,
`cover-prose.allow`, `impersonation.allow`. A `*.md` glob, or "any dotfile", would have made the
inert claim for files nobody had read.

## Blind spot 2 — a disagreement answered by narrowing nothing

`NOOP_DIRS` is **hand-written in two repositories**: here, and in each caller's
`scripts/affected-modules.py`. The platform parses the caller's literal out of the source (importing
it would run its argparse) and compared the two for **equality** — and answered any difference with
a blanket full run.

That switched narrowing off, fleet-wide, invisibly. Measured 2026-09-07, MeshWeaver.SocialMedia run
34122662676:

```text
scope: full — this script's NOOP_DIRS has drifted from scripts/affected-modules.py's —
only theirs: (none); only ours: app.
```

…on **every pull request that repository ever opened** since `app` was added here — for a repository
that has no `app/` directory at all, so not one changed file could ever have been classified by the
entry the two sides disagreed about. MeshWeaver.Manufacturing and MeshWeaver.Reinsurance carry the
same divergence today, and it is dormant in both for the same reason.

The remedy is the **intersection**, which is safe in both directions where equality never was:

- a directory only the platform calls inert — the caller believes it reaches its gate, so the
  platform must not skip it: the intersection drops it, and it goes to EVERYTHING;
- a directory only the caller calls inert — the platform believes it reaches a module, so it must
  not skip it: the intersection drops it, and it goes to EVERYTHING.

Either way the divergent directory is **over-built, out loud, and every other directory keeps
narrowing.** "The two must classify a top-level directory identically" was the right requirement
expressed as the wrong remedy: they need only agree *where it matters*, and the intersection makes
them agree by construction.

**One thing is still a refusal, and must stay one.** A caller whose `NOOP_DIRS` literal cannot be
READ — renamed, moved, reformatted onto several lines — leaves the platform unable to know what the
caller believes, and "cannot verify" has exactly one honest answer: the full set.

## Both denominators, printed

`selected` answers what is BUILT. A floor-only entry — one the caller's gates compose on every run,
which this diff does not reach — is built and deliberately **not tested**, so "how many suites will
run" is a different number. A reader who sees only the first cannot tell *nothing needed testing*
from *the filter is broken and matched nothing*. Both are now stated, with names, at the point the
decision is made and again in the job summary:

```text
scope: narrowed — 4 of 4 module bundle(s) are reachable from this diff (1 file(s));
                  0 of those owe a test run.
built 4 of 4 bundle(s): MeshWeaver.AI, MeshWeaver.Maps, MeshWeaver.Markdown.Collaboration, MeshWeaver.Payments.Stripe
tested 0 of 4 selected bundle(s): <none> — no selected bundle owes a test run
```

## What this does NOT touch, and why

**A skipped module suite still cannot be mistaken for a passed one.** Nothing here changes the
structure that guarantees it, and that structure is not an `if:` — it is receipts. The pack leg
records in its own receipt which lane owns each module's suite (`none` / `inline` / `lane`), the
tests lane drops a receipt of its own, and `node-repo-pack-verify.py` — read by `verify`, the
lane's one stable required context `All selected bundles built`, which runs `always()` — fails the
run when a module the pack leg DELEGATED has no test receipt, or when a test receipt arrives for a
module that delegated nothing. Two mutually exclusive `if:`s can both be false; the receipts are
what make that impossible to do quietly. Moving an entry from tested to untested moves it through
that same accounting, not around it.

**`clients/` stays on the loud path**, deliberately, and it is the largest remaining bucket:
measured over 120 first-parent merges to MeshWeaver.Plugins' `main`, 45 took the full-set fallback,
and the first global path was `.github/` in 25 of them, `clients/` in 11. `clients/` is absent from
`NOOP_DIRS` on purpose — *a client asset can be embedded by a host* — and the set feeds the mesh
gate and the bake as well as this lane, so narrowing it is a separate decision with a wider blast
radius than the module matrix. The number is recorded here so that decision can be taken on
evidence rather than re-derived.

**`.github/workflows/ci.yml` stays on the loud path too**, and it is the biggest single trigger.
That is correct: the caller's workflow file carries the module matrix, the platform pin and both
image digests, and a pin move genuinely invalidates every bundle in the repository.

## The guards

Two, and each was made to fail before it was believed.

**`node-repo-scope.py --self-test`** runs unconditionally in the `select` job of every satellite's
every run, next to the answer it produces, and again in core's own `dotnet-test.yml`. 63 cases.
The load-bearing ones for this change:

- every name in `NOOP_FILES` narrows to nothing on its own — asserted by iterating the set itself,
  so a name cannot be added without an assertion;
- 🚨 **and the set's own denominator is asserted beside them.** Iterating `NOOP_FILES` means
  DELETING a name would delete its case and the sweep would stay green having stopped covering it
  — the "a guard whose subject moved while its roots did not" shape. One extra case pins the names
  that were measured, so a removal turns that case red instead of quietly shrinking the population;
- an UNNAMED root file — a build input, a gate allow file, something nobody has classified yet —
  still runs the full set;
- both divergence directions narrow on the agreed directories while the divergent one is
  EVERYTHING;
- a RENAMED `NOOP_DIRS` literal still refuses to narrow at all.

**`check-noop-scope-parity.py`** runs in `node-repo-validate.yml`, so it reaches every satellite at
that satellite's `platform-ref`. It imports the platform's sets from `node-repo-scope.py` rather
than re-declaring them — a third hand-copy is the defect it exists to find — and it reds when:

- the caller's `NOOP_DIRS` literal cannot be parsed (narrowing is off in that repository and
  nothing else anywhere says so);
- a top-level directory **the caller's tree actually has** is classified differently by the two
  sets. Scoped to what exists on purpose: `app` missing from three satellites' copies is reported
  as *dormant*, not red, because none of them has an `app/` and a gate that reds about nothing is
  one people learn to ignore.

It reads two files and a directory listing — no network, no credential, no secret — so it runs on
fork pull requests exactly as it does anywhere else.

### Falsification

Each mechanism was reverted and the suite observed to go red before being restored:

| broken | red cases |
|---|---|
| the `NOOP_FILES` branch deleted from the classifier | 12 |
| `AGENTS.md` removed from `NOOP_FILES` | 4, including the denominator case |
| the intersection reverted to the old equality refusal | 2 |
| the closed list widened to "any root file" | 5, including the gate allow file |
| the parity guard's own six branches | each observed red on its own fixture |

## Measured effect

For the diff that prompted this — MeshWeaver.Plugins run 34158493654, `AGENTS.md` alone:

| | before | after |
|---|---:|---:|
| jobs | 105 | 31 |
| job-minutes | 344.9 | ~130.7 |
| `Module bundles / Module tests (…)` | 34 jobs · 152.1 min | 0 |
| `Module bundles / Module bundle (…)` | 34 jobs · 34.4 min | 0 |
| `Module bundles (floor) / Module tests (…)` | 4 jobs · 19.2 min | 0 |
| `Module bundles (floor) / Module bundle (…)` | 4 jobs · 3.0 min | 4 jobs · 3.0 min — the gates compose them |
| `Portal hosts (shard N)` | 4 jobs · 90.5 min | 4 jobs · 90.5 min — unchanged, and now the critical path |

A module-touching pull request is **unchanged**: `src/MeshWeaver.Teams/…` selected 1 of 34 and
tested 1 before this change and after it.

`Portal hosts` is the largest remaining per-PR cost and does not narrow at all — it builds the three
moved portal hosts and runs 48 platform suites on every pull request regardless of the diff. That is
a separate decision, on a job whose suites cover the platform pin rather than this repository's
modules.


## Separate compiled validation from content publication (2026-09-09)

A node-only lesson change does not require rebuilding portal hosts or running unrelated storage,
AI and Blazor suites. `node-repo-project-scope.py` selects host builds and tests from the caller's
project graph, linked content and explicit runtime-input prefixes. The caller retains its project
inventory as policy. A source-scanning guard declares the paths it reads; a linked content folder
is an input even when it lives outside `src/`. Unknown paths, absent graphs and missing diffs run
full validation; malformed or missing inventory entries fail. Selection and execution must be
reconciled by the caller's stable required check.

The module lane accepts an optional `publication-base` on main pushes. Obtain it only from
`node-repo-publication-base.py`, naming the caller's complete publishing workflow. It reads a
successful completed **main push**, verifies that commit is an ancestor, and otherwise supplies
no baseline. Manual runs cannot advance it. A newer failed, cancelled, active or release-follow
publication forces a full build: its partial writes may have used a different toolchain override,
which a source diff cannot see. Successful runs record their actual resolved platform ref and
image digests in `publication-inputs`; a missing or differing receipt also forces a full build.
The registry's package version and `github.event.before` are not publication evidence.

Selection takes the **union of paths changed by all intervening commits**, not just the net diff.
A failed run can publish some bundles before it fails; if a later commit reverts that change,
those bundles must be republished even when HEAD equals the successful baseline. The history union
retains that repair. A missing baseline or an empty/unreadable history selects everything. A
workflow/tooling/platform-pin change also selects everything. Release-follow events remain full.
Floor bundles required by compilation and the sealed bake remain selected; suites run only for
entries actually affected. `publication-run` pairs the attested baseline with its successful run.
Unchanged floor entries reuse that run's still-live module artifacts through the existing reuse
and receipt path. Missing artifacts retain the normal build leg; affected entries never reuse on
this proof. The module-pack tool is not rebuilt when all selected bundles are reused. The existing receipt verification and supported publication endpoint
are unchanged. No registry credential or mutable publication ref is introduced.

This is deliberately a conservative first separation. A chronically failing publishing workflow
forces full builds until a successful publication restores the baseline. The content-addressed module ledger remains the mechanism for coordinating concurrent builds and
reusing results across arbitrary runs; the caller must provision its credential before enabling
it. Successful-publication reuse needs only the existing GitHub Actions read permission. Do not substitute the bake's two-module composition
index for evidence that the whole compiled catalogue was published.
