---
Name: Sharding the Node-Repo Gate
Category: Architecture
Description: "The node-repo gate ran ~60 serial installs at 18-30 minutes against a 30-minute cap, and a cap cut reports as `cancelled` — a verdict nobody can read. The fan-out that fixes it, the measurement that chose equal package counts over every weight table, and the fold that keeps ONE required context and ONE gate log."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="7" height="7" rx="1"/><rect x="15" y="3" width="7" height="7" rx="1"/><rect x="2" y="14" width="7" height="7" rx="1"/><rect x="15" y="14" width="7" height="7" rx="1"/><path d="M9 6.5h6M9 17.5h6M5.5 10v4M18.5 10v4"/></svg>
---

# Sharding the node-repo gate

`.github/workflows/node-repo-gate.yml` is the heavy tester gate every `MeshWeaver.*` content repo
runs: it installs each package into a real mesh, compiles every NodeType, renders it, and executes
its `Tests` layout area. MeshWeaver.Plugins runs it over **59 packages**, serially, in one job.

This page records why it now fans out, what was measured to decide the shape, and the two
invariants that make a fan-out safe to *require*.

## The defect: a verdict nobody can read

The job carries `timeout-minutes: 30`. Measured across **28 consecutive gate jobs** on
MeshWeaver.Plugins (2026-09-03, 12:32-16:29Z), each classified by reading its **duration and its
verdict text**, never its colour:

| what the job actually was | n | durations |
|---|---|---|
| green, full set | 12 | 17.9 – 27.6 min (median 21.9) |
| green, narrowed PR closure | 3 | 7.7 – 9.9 min |
| **cut at the 30-minute cap** | **4** | 30.3 – 30.4 min |
| real content verdict (`GATE FAILED — install: Hosting`) | 3 | 28.1, 29.4, 29.7 min |
| superseded (the whole run cancelled) | 1 | 22.9 min |
| red on its inputs (image / bundle preflight) | 5 | 0.3 – 2.2 min |

Of the **20 runs that gated the full set, 4 (20%) were cut at the cap and 7 (35%) came within two
minutes of it.** A cap cut reports as **`cancelled`**, which is byte-identical to a real
cancellation — so the gate's verdict was unreadable exactly when it took longest. Two consequences,
both measured:

- **it blocked nothing.** MeshWeaver.Plugins#1253 merged with this gate `cancelled`.
- **it took the Tests-area evidence with it.** The caller's ratchet job is
  `if: always() && needs.test-repos.result != 'cancelled'`, so a cap cut silences the ratchet too.
  On the cap-cut run 33776132232 every other one of the 61 jobs finished; only the gate was
  cancelled, and `Tests-area ratchet over the gate log` and `publish-bake` were both skipped.

🚨 **A `failure` on this gate is NOT evidence of a cap cut.** Three of the four longest reds in that
sample were genuine content verdicts. Attributing them to the cap inflates the "before" and
overstates what sharding buys — read the duration *and* the verdict.

### Why raising the cap was refused

Every CI job in the fleet is hard-cut at **45 minutes** (see [Reading CI Signals](../ReadingCiSignals)
and AGENTS.md). So raising the cap buys 15 minutes against a moving tail — and **a 45-minute cut
still reports `cancelled`**. It would make the gate slower to fail and no more readable, which is the
band-aid shape AGENTS.md forbids: raising a bound instead of finding what is not completing.

### What the time actually goes on

Broken down from run 33758875713 (green, 22.0 min, full 59 packages) and its `gate-log` artifact:

| phase | seconds |
|---|---|
| set up job, checkout, ACR login | 9 |
| pull both images + extract their `/app` | 61 |
| identity, compose host, module bundles | 3 |
| `compile` (mesh-free bake of the whole mount) | 72 |
| the gate: 59 sequential installs + compile/render/Tests waits | 1 167 |
| uploads | 3 |

Of the gate's 1 167 s, **902 s attribute to individual packages** and 265 s sits in the gaps between
them (~4.5 s each). The per-install cost is the `portal/nodeops` tax of
[Bake Seal — NodeOps Saturation](../BakeSealNodeOpsSaturation): the mesh's ONE node-CRUD execution hub,
which every install goes through.

## The fix: one job per shard, one container each

Each shard is its own `docker run --rm`, so shards share **no mesh, no nodeops hub and no runner** —
the saturation does not follow them, and wall-clock falls close to linearly until the per-shard fixed
cost (image pull, `/app` extract, the compile pass ≈ 2.5 min) dominates.

```text
plan ──▶ gate (matrix: shard 1..N, fail-fast: false) ──▶ verify
 │                                                        │
 │ decides the affected closure ONCE                       │ folds the shards' logs into ONE
 │ and the shard matrix                                    │ and IS the required context
```

Modelled on the measurements above, for MeshWeaver.Plugins' 59 packages:

| shards | wall-clock | runner-minutes |
|---|---|---|
| 1 (today) | 21.9 min | 21.9 |
| 2 | 14.5 | 27.7 |
| 3 | 11.1 | 31.6 |
| **4** | **10.8** | **35.5** |
| 6 | 8.3 | 43.0 |

The model reproduces the measured 1-shard baseline to 0.1 min, which is the only reason to trust the
rest of the column. It is **opt-in per caller** (`shards:`, default `1`) precisely because the wall
saving is bought with runner minutes.

### What a shard installs versus what it gates

The gate boots a *fresh* mesh, so a package whose `requires` / `shared=@Other/Source` /
`nodeType: Other/Type` names a sibling cannot install unless that sibling landed first. A shard
therefore installs **its slice plus the forward dependency closure of that slice**, and gates only
the slice — reusing the installed-but-not-gated shape an upstream package already has
(`PackageResult.Upstream`), for the same reason: **a verdict belongs to exactly one place.**

That closure is computed by `GateShardPlan` from `LocalNodeRepo.DependencyMap` — the *same* map the
install order is sorted by, deliberately not a second reader of those three edge kinds. A second
reader is exactly how an edge silently disappears: MeshWeaver.Plugins' `scripts/affected-modules.py`
is already a mirror of that contract and had **51 of its 52 `requires` edges dead** for a while
because it compared `"Store@^1.0.0"` against a bare package id.

In MeshWeaver.Plugins the graph is a shallow star — `Store` is required by 58 of 59 packages, mean
closure 1.63, max 13 (`Collaboration`) — so the duplicated install cost is small.

### Why equal package COUNTS, and no weight table

Measured per package from run 33758875713's gate log, **every structural proxy a checkout can offer
is anti-correlated enough to lose to plain equal counts**:

| package | files | NodeTypes | measured |
|---|---|---|---|
| Store | 185 | 17 | 56 s |
| Hosting | 141 | 15 | 109 s |
| RolePlay | 38 | 4 | **129 s** |
| Chess | 37 | 3 | **111 s** |

Simulated makespan at 4 shards — equal counts **8.8 min**, files 13.4, NodeType count 14.1,
test-source bytes 16.6, and a perfect oracle 8.4. Equal counts lands within **0.4 min of the oracle**
at every shard count from 2 to 6.

So the plan carries **no weight table at all**. That is a feature, not a shortcut: contrast
`.github/scripts/shard-assign.sh`, whose table drifted for three weeks unnoticed and cost ~170 s of
long pole on every run, because an LPT loop balances the *numbers*, not the clock. There is nothing
here to re-measure and nothing to go stale.

The slice is taken by **stride** over the dependency order rather than as a contiguous block: both
give equal counts, and the stride spreads a run of adjacent heavyweights (the order is alphabetical
among the ready set, so neighbours are unrelated) across different runners.

## The two invariants that make a fan-out requirable

### 1. ONE stable context

`verify` is named `Compile + render node repos (MeshWeaver from ACR)` — unchanged — so
`<caller job> / Compile + render node repos (MeshWeaver from ACR)` keeps meaning "the whole gate ran
and was green", whatever the shard count.

🚨 **The per-shard contexts must never be what a repo requires.** `Gate shard i/n` appears and
disappears with the `shards` input, and a required context that stops being produced blocks every PR
forever. This is the same invariant, for the same reason, as `All selected bundles built` in
`node-repo-module-pack.yml`.

`verify` runs `if: always()` and fails RED naming the shard when the fan-out did not succeed — a
skipped required check reads as *satisfied*, so it must never skip.

### 2. The slices must be a DISJOINT COVER — checked from receipts

Every shard prints, **before it installs anything**, the line `GateShardPlan.Describe` builds:

```text
shard 2/4: gating 15 of 59 discovered package(s) — A, B, …; installing 3 support package(s)
gated on another shard: Store, AI, Maps
```

That is the shard's receipt, and `.github/scripts/gate-shard-merge.py` folds all N of them to answer
the one question no single shard can: **was every discovered package gated, exactly once?** It
refuses a run where a shard's log is missing, two shards claim the same package, the union is short
of the discovered total, the shards disagree about that total, a shard printed no plan line at all
(it ran without `--shard`, so it judged the whole set), or a shard's summary is truncated. Each of
those otherwise renders as a green wall — GitHub paints a skipped matrix leg exactly like a passed
one. The script self-tests all seven cases (`--self-test`).

## 🚨 Why the shard logs are FOLDED and never concatenated

The gate log is not just a transcript: a caller's Tests-area ratchet reads it
(`scripts/check-test-suites.py --gate-log`), and **that parser takes the LAST
`=== mw-plugin-test summary ===` block in the file** — deliberately, so a re-run appended to one log
reads as the current one.

Measured, by splitting run 33758875713's real gate log into 4 synthetic shard logs and running
MeshWeaver.Plugins' own ratchet over each shape:

| input to the ratchet | what it reported | exit |
|---|---|---|
| the original single log | 87 NodeType(s) across 59 package(s), 0 skipped | 0 |
| the four logs **concatenated** | **10 NodeType(s) across 15 package(s)**, 0 skipped | **0** |
| the four logs **folded** | 87 NodeType(s) across 59 package(s), 0 skipped | 0 |

The concatenation is a **false pass**: a green tick over one sixth of the evidence, produced
silently, by the very step meant to make sharding invisible. The fold merges the summaries into a
single block with one terminal verdict, dropping the `[support: …]` copies whenever the owning
shard's entry is present (a package cannot have two verdicts), and re-emits it under the canonical
`gate-log-<sha>` artifact name — so the caller's ratchet is untouched.

## Verified against the real thing, before a single CI run

The lane is `workflow_call`-only, so core's own CI never exercises it — the first honest test would
otherwise be a satellite's pin bump. It was instead run **locally against the pinned portal image
and the real MeshWeaver.Plugins tree**, composing the gate host exactly as the lane does
(`compose-gate-host.sh` over `memex-portal-ai@sha256:c4467638…` + the tester CLI) and composing the
same three module bundles (`MeshWeaver.AI`, `.Markdown.Collaboration`, `.Maps`) the caller passes as
`--module`. The one deviation: the CLI is the branch's own build, because that is where `--shard`
lives.

Over `Store, Training, Video, Northwind, Chess` (Chess depends on Training, everything depends on
Store):

```text
shard 1/2: gating 3 of 5 discovered package(s) — Store, Training, Video; installing 0 support …
shard 2/2: gating 2 of 5 discovered package(s) — Northwind, Chess; installing 2 support
           package(s) gated on another shard: Store, Training
```

Shard 2 installed `Store` and `Training` — it could not install Chess otherwise — and did **not**
judge them: they appear in its summary as `[PASS] Store (184 node(s), 0 type(s)) [support:
installed, gated on another shard]`, while shard 1 reports `[PASS] Store (184 node(s), 17
type(s))` with all seventeen verdicts.

Folding the two real logs and comparing the verdict SET against a `--shard 1/1` run of the same five
packages: **27 verdict lines each, identical.** Every package once, all 21 NodeTypes
(17 Store + 1 Training + 3 Chess) compiled, rendered and Tests-executed, `ALL GREEN.`

The earlier attempts are worth keeping, because both failures were the design working: a mount
missing `Training` failed Chess's install with `NodeType(s) not registered: Training/Tour`, and a run
without the `MeshWeaver.AI` module failed with `NodeType(s) not registered: Skill` — the same
content-shaped-error-with-an-infrastructure-cause the lane's own comments warn about.

## The pin moves with the lane

`--shard` is a flag on `mw-plugin-test`, which ships in the **tester image**. A caller that sets
`shards: > 1` therefore needs a pin from a CD wave carrying it, and the shard job asserts the flag by
name against the composed host before it gates — so a stale pin fails with a message naming the pin
rather than `Unknown argument '--shard'` naming the tester's argv.

And this lane is `workflow_call`, pinned by each caller with a 40-char commit sha: **a change here
reaches a satellite only when that satellite bumps its `uses:` and `platform-ref`.** With
`shards: 1` the command line is byte-for-byte the pre-fan-out one, so a caller that bumps the pin
without opting in sees no behaviour change at all.

## See also

- [CI Content Bake](../CiContentBake) — the lane family and the pinning contract.
- [Bake Seal — NodeOps Saturation](../BakeSealNodeOpsSaturation) — the per-install tax being parallelised.
- [Reading CI Signals](../ReadingCiSignals) — why a skipped or absent required context reads as green.
