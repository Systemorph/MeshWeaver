---
Name: A Census That Counts Must Name
Category: Architecture
Description: The one bake census that sees past row-level security counted a permanently-broken NodeType and dropped its path one call before publication, so its only output was a number nobody could resolve to an owner — which reads as clean. Where the identity was lost, what a public census may name and what it may not, and how to tell a fix that is merged from a fix that is running.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 20V10"/><path d="M10 20V4"/><path d="M16 20v-7"/><path d="M21 20H3"/><circle cx="16" cy="9" r="2.5"/></svg>
---

# A Census That Counts Must Name

`/health` is composed by the process, so it is **past row-level security by construction** and needs
no grant to read. That makes it the one instrument that can answer *"can THIS replica load NodeType
X"* when the sweep a session can run — `search 'nodeType:NodeType content.compilationStatus:Error'`
— cannot, because the sweep is RLS-filtered and the broken type is in a partition the reader holds
no grant on.

Measured on memex.systemorph.com, 2026-09-14:

```
bake-report: Healthy — compiling sweep at 2026-09-12T16:51:56.4Z: framework=sd608997
  total=215 baked=214 pending=1 previouslybroken=1
```

`previouslybroken=1` says **exactly one** NodeType on that replica sits at `CompilationStatus.Error`
and was never healthy. The caller's own sweep answered **0 over 147 readable NodeTypes** — so the
census had bounded the problem to a single type among the 68 the caller cannot read, and then
declined to say which.

## The identity existed and was dropped one call before publication

Three hops, no missing plumbing:

| | |
|---|---|
| `NodeTypeBakeEntry(string TypePath, BakeState State, string? Detail)` | every entry carries the path — `src/MeshWeaver.Compiler.Pipeline/NodeTypeBakeStatus.cs` |
| `NodeTypeBakeReport.Summary` | groups those entries by state and emits ` state=count`. The paths are in scope and discarded |
| `DynamicTypePreWarmer.PublishReport` | reduces the report to eight scalars plus that string. `Entries` never reaches `NodeTypeBakeReportRegistry`, so `Describe` could not name anything even if it wanted to |

`BakeState.PreviouslyBroken` is deliberately skipped by the rollout gate: a type that was already
broken before this image is not made worse by it, and blocking every future deploy on one abandoned
type would freeze the platform at the worst possible moment. That trade is right — but its
consequence is that **the count is the only record in the system that a NodeType is permanently
broken, and the gate is by design never going to complain again.**

So the failure mode is not a missing instrument. It is worse:

> **A census that counts a failure and drops its identity reads as clean.** Nothing is red, nothing
> is pending, and the one number that indicts resolves to nobody.

`#3883` (`BinaryClickerV2/BinaryToggle`, `CS1929`) failed to close **three times** on exactly this —
once as `#1391`, closed on a `Not found` from `get_diagnostics` that meant "denied", not "deleted";
once in a 311-issue bulk sweep that read nothing; and once with every probe a session can run
answering *"I cannot tell"*, each for its own reason and none of them "no".

## What the report now publishes

`NodeTypeBakeReport.Ownership` is the identity half of `Summary`, carried across `PublishReport` on
`BakeReportReading.Ownership` and printed by `NodeTypeBakeReportRegistry.Describe`:

```
bake-report: Healthy — compiling sweep at …: framework=sd608997 total=215 baked=214 pending=1
  previouslybroken=1. Non-baked types by partition: previouslybroken in BinaryClickerV2/… (the
  PARTITION each one lives in — this body is public and unauthenticated, so the node's own name is
  deliberately not printed; a partition routes the finding to an owner, #4258). Adoption stamps …
```

Every non-`Baked` state is covered, not only the broken one: `pending=58 frameworkstale=55` on
memex.meshweaver.cloud had the same shape and the same "nobody can enumerate them" consequence.

## The disclosure boundary — the PARTITION, never the node title

`/health` is public and unauthenticated. Naming `BinaryClickerV2/BinaryToggle` on it would disclose
a private partition's node path to anyone on the internet.

That the same endpoint *already* prints instance paths (`content-types` names `d.LastPath` for every
type it could not resolve) is a reason to weigh that surface too, **not** a licence to add to it.
`#3890` closed the `autocomplete` drill-down on exactly this principle:

> A control that works by disclosing other people's node titles is a disclosure surface wearing an
> instrument's colours.

So the census names the path's **first segment** and stops. That is enough to route the finding to an
owner, which is all `#3883` ever needed — a partition name is already visible to anyone who can list
`Admin/Partition` — and it discloses strictly less than what the same body already carries.

| | published | withheld |
|---|---|---|
| `BinaryClickerV2/BinaryToggle` at `PreviouslyBroken` | `previouslybroken in BinaryClickerV2/…` | `BinaryToggle` |
| 20 partitions framework-stale | the first 12, then `(+8 more partition(s))` | the rest, and every node title |

Naming the whole path — on `/health`, on an authenticated probe, or as an `Ops/Status`-shaped node —
is a **disclosure-policy call for whoever owns that surface**. It is now one projection away rather
than a re-plumbing: the entries reach the publication, and only `PartitionOf` decides how much of
each path is printed.

## The property the tests hold, and why the silent half matters

`/health` prints **only entries that are not Healthy**, so an absent entry is not a clean one — it is
equally "this check was never registered in this host". The two `census`-tagged entries
(`bake-report`, `source-discovery`) are the exception and print their reading **whatever the status**,
which is the only reason a clean bake is distinguishable on the wire from a bake that never ran.

That distinction is load-bearing here and must survive any change to this surface:

- A permanently-broken type **must not** flip `bake-report` to Degraded. The gate skips it on
  purpose; degrading on it would re-introduce the deploy freeze the skip exists to prevent.
- Therefore the reading prints **because of the census tag**, not because something is wrong. If
  `bake-report` ever goes silent while Healthy, the identity is dropped again — by a different route.

The guards are `HealthCensusTest.ABrokenNodeTypesPARTITION_IsNamedOnTheUnauthenticatedBody_ButNotItsTitle`
(end to end over the real health composition), `BakeCensusRegistryTest`, and
`NodeTypeBakeStatusTest.Ownership_*`. Each carries its own positive control — a clean report must
name **nobody**, or the "it names the partition" assertions could never fail — and the disclosure
assertion was watched failing with `PartitionOf` returning the whole path, so it is not a test that
asserts an absence and checks nothing.

## Reading a zero from either instrument

Neither instrument answers the other's question, and each has its own denominator.

| instrument | runs as | denominator | a zero means |
|---|---|---|---|
| `search 'nodeType:NodeType content.compilationStatus:Error'` | **you** | the NodeTypes in partitions you hold a grant on | "none of the N I can see" — never "none exist" |
| `/health` → `bake-report` | **the process** | every dynamic NodeType on **this replica** | "this replica's report has no such entry" |
| `/health` → `content-types` | the process | every type whose content a read on this replica actually degraded | "nothing has degraded **yet** here" |

Two consequences that have each cost a session:

- **State the denominator with every zero.** "0 of N over M readable partitions", never "0".
- **Repeated `/health` calls sample different replicas.** Measured 2026-09-11 on
  memex.meshweaver.cloud: 10 calls returned 2 disjoint bodies. One call answers about one replica you
  did not choose; the per-replica form with no guesswork is the `Sample` instance action, which
  carries each pod's whole `/health` body.

## Telling a fix that is MERGED from a fix that is RUNNING

A defect in an instrument has two separate questions, and the answer to the first does not settle the
second.

**Is the fix in the code?** Verify by the **symbol**, at file:line, on `origin/main` — never by a
merge-commit ancestry test, because the merge queue rewrites the sha and ancestry is a false negative
here.

**Is the fix in the running image?** The reliable instrument is rarely an image tag, which nobody
reading a log has. It is usually the **line's own text**: a fix that changes what the instrument
prints makes every subsequent occurrence self-dating. The install-completeness sweep is the worked
example — `#3685` added a population clause, so a line reading

```
Counted over: 95 file(s) declared, 12 of them not node files (README/manifest/content assets,
or an extension no parser claims) → 83 distinct node path(s) compared.
```

is *proof* that the pod emitting it is running post-`#3685` code, with no access to the deployment at
all. When you change an instrument, changing its sentence is therefore not cosmetic: it is what lets
the next reader date the reading.

🚨 **And an issue can stay open on its REOPENER rather than on its defect.** `#2157` and `#3659` were
both fixed and running, and both kept reopening because the incident that points at them is minted by
a **separately shipped watcher image** whose identity function hashes category + event id alone — so
every error line a category will ever log folds onto one incident, of any shape. Closing the code
defect cannot make such an issue stay closed; that takes the watcher's own delivery. Read a recurrence
comment's **retained samples** before treating it as the defect returning: if none of them has the
defect's shape, it is a fold, not a recurrence.

## Related

- [Adoption and the Sweep Count Different Things](../AdoptionAndTheSweepCountDifferentThings) — the other half of this census's numbers: why 78 and 5 were never the same population
- [Which Kind of Silence](../WhichKindOfSilence) — the same reading problem one level down: four causes of a quiet log with four owners
- [NodeType Compilation](../NodeTypeCompilation) — what puts a type at `CompilationStatus.Error` in the first place
- [Operating from the Portal](../OperatingFromThePortal) — the instance actions that read a specific replica rather than an arbitrary one
- [Access Control](../AccessControl) — why an RLS-filtered read's zero is not an answer
