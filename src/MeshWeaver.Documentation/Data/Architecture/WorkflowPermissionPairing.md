# A caller grants what its lane demands

🚨 **A `permissions:` block inside a `workflow_call` workflow is not a grant. It is a requirement
imposed on every caller, in every repository — including repositories the lane's own repo cannot
see.** This is true of a **job-level** block and equally of a **workflow-level** one.

A called workflow's job can never hold a permission its caller did not grant. So adding
`permissions:` to a shared lane silently makes it a precondition fleet-wide, and a caller that does
not meet it does not get a permission error.

## What it looks like when it goes wrong

**The run graph is rejected before a single job is scheduled.** `startup_failure`, **zero jobs**, and
therefore **no check-run published at all**. Under classic branch protection an *absent* required
context blocks forever rather than passing (see [Reading CI Signals](../ReadingCiSignals)), so the
repository cannot merge anything — including the fix for the thing that broke it.

There is no error text naming the permission. The symptom is an absence.

🚨 **A `startup_failure` rejects the WHOLE WORKFLOW FILE's graph, not the offending job.** Every
satellite calls its lanes from one `ci.yml`, so one unpaired caller takes down every other job in
that file — including the jobs that publish the repository's required contexts. This is also why the
guard cannot rescue the satellite it runs in; see "What the guard does not see" below.

## The incident this is written from (2026-09-10)

MeshWeaver#3933 added `permissions: {contents: read, id-token: write}` to three jobs of
`node-repo-module-pack.yml`, so core's CD could authenticate to ACR over OIDC without a long-lived
credential. It updated core's own caller in the same commit, and **no satellite**.

| | |
|---|---|
| 18:54:37Z | run resolves core `@main` = `756d6d99e` (pre-#3933) → jobs ran |
| 18:57:53Z | #3933 merges as `846bfbc90` / `d540b0dca` |
| **18:59:37Z** | run `34517698518` resolves `d540b0dca` → `startup_failure`, **`total_count: 0` jobs**, `path=.github/workflows/ci.yml` |
| 18:59Z → 22:56Z | MeshWeaver.Plugins dark: **28** zero-job runs, main, every PR, every trigger |
| — | MeshWeaver.SocialMedia had the identical gap, unfired only because its lane had not run |

`id-token` is used only on the opt-in `acr-login: oidc` path; the default is `basic`. So every
satellite was made to require a permission for a code path none of them take.

Fixed by MeshWeaver#3968, which removed the job-level blocks and restored caller inheritance.

## The rule

For every job in a repository that `uses:` a shared lane, the caller's effective permissions must be
a **superset** of every `permissions:` block — job-level *and* workflow-level — that the lane
declares. `.github/scripts/check-workflow-permission-pairing.py` enforces it.

## 🚨 The audit that corrected this page (MeshWeaver#4011)

The guard as first shipped (#3989) ran in one mode: **pairing** — both halves in one checkout. That
mode is real and still runs, but on its own **it would have caught the outage in exactly zero
repositories**, and this page previously implied otherwise. Three measurements settle it.

**1. In core's own pull request the pairing check is silent, and correctly so.** Core holds exactly
**two** caller→lane edges in its whole workflow tree, both in `main-cd.yml`, and both already grant
`id-token: write`. Re-injecting #3933's literal shape (`permissions: {id-token: write}` on
`node-repo-module-pack.yml`'s `pack` job) leaves the pairing check at **exit 0** — it resolves the new
demand as a *third* pair and finds core's caller satisfies it. Nothing is wrong with that verdict;
the callers that break are simply not in core. Both #3933 and its predecessor updated core's own
caller in the same commit, which is what makes this silence systematic rather than unlucky.

**2. The satellite run cannot rescue the satellite.** The pairing check is hosted in
`node-repo-validate.yml`, which every satellite calls from a `validate` job in **the same `ci.yml`**
that calls the other lanes. A `startup_failure` rejects that file's entire graph, so on a recurrence
the guard's own job is never scheduled. The guard cannot report the outage it is standing in.

**3. There is no pin to slow it down.** Measured 2026-09-11 on live default branches: **five of the
six satellites call the lanes at `@main`** (only MeshWeaver.Education still pins a platform sha). A
core merge therefore reaches them within seconds.

So for #3989's guard a recurrence was **MISSED** — not prevented, and not merely detected late.

### 🚨 But a DIFFERENT guard did cover the narrow case, and that must not be misread either

`.github/scripts/test-cd-steps.py` — added by MeshWeaver#3968's follow-up, not by #3989 — asserts
that `node-repo-module-pack.yml` declares **no** `permissions:` at all, workflow- or job-level. It
runs in core's `dotnet-test.yml` on every pull request, and re-injecting #3933's exact shape does red
it. So the literal recurrence *was* prevented in core's own PR, by that check.

Its cover is narrow by construction: it hard-codes **one lane** and **three job names**
(`prepare`, `build-workspace`, `pack`). Measured on this tree by mutating every job of every
`workflow_call` lane in core and asking each guard:

| | lane jobs it reds on |
|---|---|
| `test-cd-steps.py` | **3** of 22 (`node-repo-module-pack.yml` only, and only 3 of its 6 jobs) |
| the fleet roster | **17** of 22 — every lane job that has a recorded caller (16 before MeshWeaver#3878 recorded `node-repo-module-publish`'s first caller) |

`node-repo-module-pack.yml`'s `select`, `tests` and `verify` are in that lane, break the fleet
identically, and were covered by neither before this change.

## What closes it: the fleet roster

The callers that break live in repositories core's pull request cannot read, and reaching them with a
credential would make the gate skip on forks — which
AGENTS.md forbids outright. So their committed grants are recorded in
**`.github/lane-caller-grants.yml`** and checked in core's own PR. It is the same inverted-ordering
device as `Pairs-with:` and `Implementers:`: the core half lands first, so the coupling is *declared*
rather than discovered.

The guard has three modes:

| mode | runs in | answers |
|---|---|---|
| `--root R [--platform-root P]` | every satellite | do *this repo's* callers pair with the lanes it calls? |
| `--fleet ROSTER --root R` | **core** | does a lane change break a caller in a repo core cannot read? |
| `--assert-fleet-row REPO --fleet F` | every satellite | does the roster still describe *this* repo? |

🚨 **The third mode is what keeps the second honest.** A roster nobody re-measures is a memory, not a
measurement — the instrument that answers confidently from stale knowledge. Both drift directions
would make the fleet check green on a broken fleet (a repo **adds** a caller the roster does not know;
a repo **lowers** a grant the roster still records), and both red in a job that *does* run, because a
roster mismatch is not a permission escalation and so the run graph is accepted.

**Regenerate a row** in a checkout of that repository:

```bash
python3 check-workflow-permission-pairing.py --root . --emit-fleet-row Systemorph/<repo>
```

## A caller that is arriving: `pending:`

A satellite gains a caller of a platform lane in two repositories, and the roster made that
unlandable. Every satellite asserts its row against core's `main` (`scripts-ref: main`), so under
strict equality:

| lands first | what reds | where |
|---|---|---|
| the roster row | "the roster records a caller this repository does not have" | every pull request of the satellite, until its half merges |
| the satellite's caller | "a caller the roster records no row for" | the satellite's own pull request, which cannot merge |

`validate / Validate node repos` is a required context in every satellite, so either order is a
repository-wide block. Found while wiring MeshWeaver#3878's `publish-modules` job into
MeshWeaver.Plugins — whose `main` was already red on a host shard, so "the window is short" could
not be assumed.

So a row may carry `pending: <reason naming the change that lands it>`, and the roster row lands
**first**, like `Pairs-with:` and `Implementers:` — which is what lets core's own pull request check
the new pair's grant before either half merges. The marker is deliberately narrow:

| state | verdict |
|---|---|
| `pending:` row, caller not in the repository yet | passes, and prints the reason |
| the same row with no marker | **red** — a caller deleted and its row forgotten describes a repository that does not exist |
| `pending:` with an empty reason | **red** in `--assert-fleet-row` **and** in `--fleet`, so core refuses it before any satellite sees it |
| `pending:` row, caller present with exactly its grant | passes; the note says the marker is spent |
| `pending:` row, caller present with a lower grant | **red** — the marker excuses absence only, never a mismatch |

`--fleet` checks a pending row's grant like any other and prints it as `PENDING:`, not under
`NOT COVERED:`. Once the satellite's caller merges, a core follow-up removes the spent marker.

## What the guard does not see

A reader should not leave this page believing it covers more than it does.

- **It does not see a repository that is not in the roster.** Coverage is exactly the roster's rows.
- **It does not see a lane with no recorded caller.** Three lanes / **5 of 22 lane jobs** are in
  that state today (`node-repo-platform-canary`, `node-repo-platform-ref-bump`, `plugin-build`;
  `node-repo-module-publish` left the list when MeshWeaver#3878 recorded its first caller). They are **printed as `NOT COVERED` on every run**
  rather than folded into the green, because a lane with zero known callers is a coverage gap, not a
  clean measurement.
- **A silent lane job is not always a gap, and the two causes must not be conflated.** Adding
  `id-token: write` to `node-repo-gate.yml#gate` reds nothing — correctly, because all six of its
  recorded callers already grant it. Adding `packages: write` to the same job reds. A green there
  means *this demand is already paired*, never *this job is unchecked*; the `NOT COVERED` lines are
  what name the genuinely unchecked ones.
- **`Systemorph/Memex` is recorded but not re-measured.** It calls only `auto-arm.yml` and has no
  `node-repo-validate` caller to run `--assert-fleet-row`, so its row is a snapshot. It carries
  `asserted-by: none — …` and prints under `NOT COVERED`. Closing that needs a step in Memex's own CI.
- **It does not see a caller in a repository outside the fleet**, nor a private fork.
- **It is a static read of committed YAML.** It does not resolve `${{ }}` expressions, and it cannot
  know what a repository-level default permission is actually set to — which is why absence is scored
  against a conservative floor rather than assumed permissive.

## Two things the guard deliberately gets right

**It does not ban `permissions:` in a lane.** Measured on core 2026-09-11: eleven of twelve
`workflow_call` workflows declare them, and `node-repo-gate` and `node-repo-publish-bake` both demand
`id-token: write` with every one of their fleet callers correctly paired. Demanding a permission is
legitimate; demanding it without pairing the callers is the defect.

**An absent `permissions:` block is not "grants nothing".** The job inherits the repository default,
whose floor is `contents`/`packages`/`metadata: read`. The first version of this guard treated
absence as zero and produced, against a real satellite, **one true finding and two false ones** — and
a guard that fires on working lanes gets muted, which is how the next real one goes unnoticed. What
the default can *never* provide is `id-token`, which always needs an explicit grant — and that is
exactly the scope this incident turned on.

## The controls, and why they are derived from the real tree

🚨 **#3989's self-test asserted `"the 2026-09-10 outage shape is caught"` and passed, while injecting
that exact shape into the real lane file changed nothing.** Both were true at once: the case tested
`_satisfies()` against two hand-written dicts, and the tree it claimed to be about was never read.
That is the textbook *guard whose subject moved and whose roots did not* — it passed having checked
nothing about the thing it names.

`--self-test` now takes `--root` and `--fleet`, and its controls **mutate the real lane files**:

| control | direction |
|---|---|
| the unmodified tree pairs cleanly, with a non-zero pair count | green must be earned |
| a scope the real caller lacks, injected into a lane it really calls, is caught **and names that caller** | red |
| the unmodified tree passes the fleet roster (46 pairs) | green must be earned |
| **`id-token: write` on `node-repo-module-pack.yml#pack`** reds the fleet check and names the ungranting callers | red |
| a checkout rebuilt from a recorded roster row matches it | green must be earned |
| a repo that lowers a grant / adds a caller / is missing entirely | red |
| a `pending:` row whose caller has not landed / a spent marker with exactly its grant | green must be earned |
| the same row with no marker / an empty reason / a lowered grant behind a marker | red |
| an empty `pending:` reason in core's own fleet run | red |

The falsification that closes the loop, run on this tree:

| | pairing mode | fleet mode |
|---|---|---|
| `id-token: write` injected on `pack` | **exit 0** (3 pairs, 0 violations) | **exit 1** — names `node-repo-module-pack.yml#pack` and `MeshWeaver.Plugins ci.yml#modules-floor` |
| tree restored | exit 0 | **exit 0** (45 pairs) |

The mode the self-test ran in is **printed**, and so is the denominator (`N pair(s) resolved`), green
or red — a check pointed at the wrong root cannot tick like a clean measurement.

🚨 **The fleet controls run only in the checkout that owns the lanes.** A satellite's `--root` holds
callers and no lane, so asking it to run them reds it for a true statement. That is a property of the
*tree*, not of a secret or an event; it is printed either way; and core runs them on every pull
request. This is not a skip-trapdoor — it was found by simulating the satellite lane before merging,
which failed three controls at once and would have reddened `Validate node repos` in all six
repositories.

## A defect this audit found in the guard itself

A cross-repo reference — `Systemorph/MeshWeaver/.github/workflows/auto-arm.yml@…` — was resolved by
**basename inside the calling repository first**. Every satellite carries its own two-line
`.github/workflows/auto-arm.yml` *caller wrapper* whose basename equals the core lane it calls, so
the guard read the wrapper as if it were the lane and **compared a caller against itself**, reporting
a pair that was never checked. A `./x.yml` reference now resolves only in the calling repo and an
`owner/repo/...` reference only in the platform checkout, and `auto-arm` was added to the lane set
the satellite fetches so that caller is genuinely covered.

## Related

- [Reading CI Signals](../ReadingCiSignals) — why an absent required context is not a skipped one.
- [Cross-Repo Pair Gate](../CrossRepoPairGate) — the same family: a change whose other half lives in
  a repository this one cannot see. A new **required input** on a reusable workflow is the other
  known instance of this exact shape.
