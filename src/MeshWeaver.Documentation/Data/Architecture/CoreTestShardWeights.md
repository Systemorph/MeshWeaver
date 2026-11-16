---
Name: Core Test Shard Weights
Category: Architecture
Description: >-
  How core's eight test shards are balanced: a weight table generated from measured runs
  (.github/scripts/shard-weights.txt, by shard-weights.py refresh), per-part weights for split
  projects, a per-PR check that every test project lands on exactly one shard, and a weekly check
  that the table still matches the clock. Why the table stopped being hand-edited, and what it does
  not see.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="4" height="16"/><rect x="10" y="8" width="4" height="12"/><rect x="17" y="6" width="4" height="14"/></svg>
---

# Core Test Shard Weights

Core's `MeshWeaver Build and Test` runs its test projects across eight `Run tests (shard k)` jobs.
Which project runs where is decided by `.github/scripts/shard-assign.sh`: greedy LPT, heaviest
unit first onto the least-loaded shard. The build job packs each shard's bins with the same script,
so the two always agree.

## Why the table is generated, not edited

LPT balances the NUMBERS in its weight table, not the clock. A stale table therefore keeps printing
eight even loads while one runner carries the long pole, and nothing names the table as the cause.
It happened every time the table was edited by hand:

- the 2026-07-13 and 2026-08-04 tables drifted for weeks (the second cost ~170 s of long pole per
  run);
- the 2026-10-23 table printed loads of 389–396 s on every shard on run `43742571208`, while the
  shards ran **4.8 to 9.6 min** (medians over 136 runs, section 4 of the November 2026 CI profile,
  `Doc/Architecture/CiProfile2026-11` on `main`).

So the table is now a file a script writes.

## The pieces

| Piece | What it does |
|---|---|
| `.github/scripts/shard-weights.txt` | The table. `<seconds> <project> [parts]` is a project's total and its split; `<seconds> <project>#<k>/<n>` is one measured part. The header names the runs it was measured from and when. |
| `shard-weights.py refresh` | Reads the newest green runs over REST, pairs each unit's `[CI] HH:MM:SS ▶ <label> starting` line with the `[CI] <name> exit=0 elapsed=<s>s` marker that ends it, and writes the table. A unit's weight is the **max of its newest three green observations** (sized for the bad case; the headroom guard reads the same numbers). |
| `shard-assign.sh` | Schedules a split project's parts at their own measured weight. The every-Nth class split is uneven (one part of `Graph.Test` measured 360 s beside 143 s), and a total ÷ N weight let two heavy parts land on one shard. A part with no row for the current parts count falls back to total ÷ N. |
| `shard-weights.py check-assignment` | Runs on every PR, in the whole-tree guards job of CI's own shell (never path-selected). It runs `shard-assign.sh` for all eight shards under `TEST_SELECTION=all` and under an incremental selection, and reds unless every test project, and every part of a split one, is assigned **exactly once**. |
| `shard-weights.py --self-test` | Proves the parser, the aggregation and both checks can fail: a script that drops a project and one that duplicates one are both named, and an empty tree is not a pass. |
| `.github/workflows/shard-weights.yml` | Weekly. Re-measures and reds when a unit drifted by ≥ 25 % **and** > 30 s, keeping one standing issue labelled `shard-weights-sentinel`. Never a PR check: a green shard must not go red because a project got slower. |
| `ShardWeightHeadroomGuard` | Unchanged rules, now reading the table file: no project's total ÷ parts may pass 60 % of the 8-minute per-unit cap, and the ideal load must fit the shard job's cap. |

## Refreshing

```bash
python3 .github/scripts/shard-weights.py refresh          # writes .github/scripts/shard-weights.txt
python3 .github/scripts/shard-weights.py check-assignment # predicted loads + the exactly-once check
```

Changing a project's parts count is a decision (the SPLIT RULE in `shard-assign.sh`), made by hand
in the table. The next refresh keeps the count and measures the new parts once they have run.

## The first refresh (2026-11-15)

26 green `main` runs (pushes to `main` and pull requests into it), 24 units. Against those
measurements the old assignment's heaviest shard was 537 s and its lightest 192 s, the same shape
as the 4.8–9.6 min shard medians in the profile. The new assignment's predicted heaviest is 334 s,
with a spread of 16 s across the eight shards. That is an estimated **−3 min** off the test stage of
every core PR that runs the full suite.

`MeshWeaver.Hosting.Test` measured 276 + 326 s in its two parts on run `43742571208`, past the 288 s
headroom line, so it runs in three parts again. Its new parts have no measurement yet, so its total
is the one carried from the first, unfiltered reading (702 s), which overstates it.

**This line (`main3`)** was measured from 24 green `main3` runs: the old assignment's heaviest shard
weighed 635 s against those weights, the new predicted heaviest 473 s (spread 18 s), about
**−2.7 min**. `Hosting.Test` (707 s) runs in three parts and `Hosting.Orleans.Test` (291 s, past the
288 s line) in two, both scheduled at total ÷ N until measured.

**Each delivery line keeps its own table.** The same suites run markedly slower on `main3` (v3)
than on `main` (v4): `Messaging.Hub.Test` part 1/3 measured 361 s on `main3` and 111 s on `main`,
and `Hosting.Orleans.Test` 291 s against 96 s. A table measured over both lines is wrong for each, so
`refresh` and `check-current` take `--line` (default `main`). A pull-request run whose pull request
has merged carries no `pull_requests` entry, so its base is read from the pull request of its head
branch.

## What it does not see

- **Per-part headroom.** The headroom guard still divides a project's total by its parts. On `main`,
  `Graph.Test` part 2/4 measured 323 s on its own, past 288 s. Holding the guard to per-part weights
  would force that split. That is left for a separate decision.
- **Max of three is noisy.** One slow runner raises a unit until three newer observations replace
  it.
- **New parts are unmeasured.** A re-split project is scheduled at its carried total ÷ N until a
  refresh after its first runs.
