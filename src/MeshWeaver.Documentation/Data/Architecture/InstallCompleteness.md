---
Name: Install Completeness
Category: Architecture
Description: An install that half-lands must not read as a complete one. What the install record DECLARES landed, compared against what is actually in the mesh — the comparison nothing made until #3485, the five verdicts it can reach, and why only one of them is a pass.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 5h13M3 10h9M3 15h6"/><path d="M15 15l3 3 5-5"/></svg>
---

# Install Completeness

> A shipped package was missing a source node on a production portal for **eleven days**. The
> compile record showed **4 matched Code nodes against the bake's 5**. A reinstall did not restore
> it. Every instrument the platform had reported the install as healthy, because **nothing compared
> what landed against what the install declared**.
> — MeshWeaver#3485, the proximate cause of the #3472 outage

## The shape of the defect

An install produces three numbers, and **not one of them is an observation of the mesh**:

| Number | Where | What it actually counts |
|---|---|---|
| `InstallResult.Total` | `PackageInstaller.InstallNodeRepo` | the files the installer PARSED |
| `InstallResult.Written` | same | the writes the installer DECIDED to issue |
| `PackageManifest.InstalledNodeCount` | the install record | `Total`, copied — and it had **no reader anywhere in the repo** |

And the gate that decides whether a reinstall does anything compared two **content hashes**:

```csharp
// CatalogLayoutAreas.InstallOrUpdateCore — before #3485
if (record is not null
    && string.Equals(record.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal))
    return WithModule(Observable.Return(new InstallResult(0, 0)));   // nothing to sync
```

`record.ModuleVersion` is a hash over the FILES THE SOURCE SERVES, stamped by the installer onto a
record the installer itself wrote. It is a true and useful statement — about the source. It says
nothing whatever about the mesh. So when a node went missing *after* the install, the two hashes
stayed equal, and the one remedy an operator reaches for returned `InstallResult(0, 0)` **without
fetching a single file**.

> 🚨 This is the governing rule of the whole page. **When a check answers a BOOLEAN about something
> it had to READ, ask what it answers when the read fails — or when it never read at all. If that is
> indistinguishable from a real negative, that is the bug.** Here the gate never read the mesh, and
> "the install is whole" was spelled identically to "the record says it was whole when it was
> written".

## Two shapes, one mechanism

Both were measured on `memex.systemorph.com` on 2026-09-07.

### 1. A declared node that is not there

```text
Plugins/Feedback   installedNodeCount: 14, installedFiles: 14 entries
                     …/Feedback/Guide.md
                     …/Feedback/Feedback/Source/FeedbackContent.cs
namespace:Feedback scope:descendants  →  9 nodes
                     Feedback/Guide                              ABSENT
                     Feedback/Feedback/Source/FeedbackContent    ABSENT
```

The record already carries the declaration. Nothing ever read it back.

### 2. A partition root that no install record accounts for

A node-repo install writes the partition ROOT first, as a bare `Space` placeholder with no content,
and stamps the install record LAST:

```csharp
// PackageInstaller.InstallNodeRepo — stage 0
var placeholderRoot = root is not null && !rootTypeIsStatic
    ? root with { NodeType = "Space", Content = null }
    : null;
```

An install that dies in between leaves a root the portal serves as an ordinary empty space, with
nothing anywhere saying an install was attempted. Measured:

```text
nodeType:Space, no install record, version 1, only a _Policy child
  AgenticPrimerDe    created 2026-09-06T10:55:37Z
  DataImportExport   created 2026-09-06T10:55:38Z
  DataModeling       created 2026-09-06T10:55:49Z
  ThinkInStreams     created 2026-09-06T10:55:52Z
```

Four roots inside one fifteen-second window. On the authoring mesh the same paths are `Store/Plugin`
nodes at version 34 and 36 with content. The record-driven comparison cannot see this by
construction — there is no record to compare against — so it is the second arm of the same sweep.

## The contract

`InstallCompleteness` (in `MeshWeaver.PluginCatalog`) answers one question, and it has **five**
answers because four different things can go wrong and only one outcome is fine.

| Verdict | Meaning | `IsComplete` |
|---|---|---|
| `Complete` | every node the record declares was OBSERVED in the mesh | **true** |
| `Incomplete` | at least one declared node is ABSENT — the install is being served partial | false |
| `Undeclared` | nothing declares what should be here: no record, no file map, or a file map that does not address this partition | false |
| `NotObserved` | the mesh could not be read | false |
| `RootWithoutRecord` | a partition root exists that no install record accounts for | false |

> 🚨 `Undeclared` and `NotObserved` are **not passes**. They are the two ways a check can answer
> without having checked, and collapsing either into `Complete` would recreate the exact defect: a
> green verdict resting on nothing. `IsComplete` is true for `Complete` and nothing else.

**Declared side.** `PackageManifest.InstalledFiles` — the per-file hash map CI generates as
`manifest.lock` and the installer stamps onto the record — mapped through
`PackageInstaller.NodePathForFile`, the same file→node mapping the incremental update path uses.
Non-node files (`README.md`, the manifest sidecar, `content/**` assets) map to `null` and are
excluded: a content asset is not a node, and counting one would make every healthy install read as
incomplete.

**Observed side.** ONE batched `IStorageAdapter.ReadMany` over exactly the declared paths.

- Never a **query**. Queries are eventually consistent, and a stale negative here would manufacture
  a shortfall out of index lag — see [CQRS — Queries vs. Content Access](../CqrsAndContentAccess).
- Never **N point reads of paths that may not exist**. A point read of an absent node answers a
  routing NotFound that terminates the stream *and opens the storm breaker on that path*, which
  then fast-fails the very write that would heal it.
- A read that faults yields `NotObserved`, never "absent".

🚨 **And that rule applies to the sweep's own machinery, not only to the packages it checks.** The
first version of this change folded "no storage adapter" and "the root listing faulted" into an empty
sequence, so the summary printed `0 root(s) with no record` in both cases — indistinguishable from a
clean sweep, which is this page's whole subject reproduced inside its own implementation. Caught in
review, and now:

| Situation | What the sweep emits |
|---|---|
| no storage adapter | one `NotObserved` — "the abandoned-root sweep did NOT run" |
| the root listing faulted | one `NotObserved` naming the fault |
| more unaccounted roots than the bound | one `NotObserved` naming the count |
| a candidate root whose children could not be listed | one `NotObserved` for **that root** — not an accusation, and not silence |
| a clean sweep with nothing to report | **zero** verdicts |

Only the last line is a zero, and `ASweepThatCouldNotRun_SaysSo_InsteadOfReportingZero` pins it —
falsified by restoring the empty sequence, which turns it red.

## Where the verdict is consumed

### The install gate — it heals

`CatalogLayoutAreas.InstallOrUpdateCore` still compares the module hash first (it is cheap and it is
the right question about the source), but the skip now requires a **positive observation**:

| Verdict | What happens |
|---|---|
| `Complete` | skip — logged at Information with `{Present}/{Declared}` |
| `Incomplete` | **do not skip.** The full install runs and heals it — `DecideAndWrite` writes whenever `current is null` — and the shortfall is named at **Error** with the missing paths |
| `Undeclared` / `NotObserved` | skip, as before, but logged at **Warning** saying completeness was NOT verified and why |

The last row is deliberate and worth stating plainly. Reinstalling every unverifiable package on
every boot would be a new cost nobody asked for, so the behaviour is unchanged — but the *report*
is not: an unverified install is never counted as a verified one. As records gain file maps on their
next real install, that row empties on its own.

### The boot sweep — it reports

`InstalledPackageRepairService` already holds the one complete inventory of what is installed and
already runs once per boot, fire-and-forget and failure-tolerant. It now finishes with the sweep:

```text
[InstallCompleteness] Feedback → 'Feedback': 2 of 13 declared node(s) are ABSENT.
    Missing: [Feedback/Feedback/Source/FeedbackContent, Feedback/Guide]. …
[InstallCompleteness] 'AgenticPrimerDe' is a partition ROOT that no install record accounts for …
[InstallCompleteness] 64 checked · 61 complete · 1 INCOMPLETE · 2 not declared · 0 not observed ·
    4 root(s) with no record (records scanned: 63; storage adapter: present)
```

**It reports; it does not repair.** Healing belongs to the install lane, which now refuses to skip an
incomplete package. A boot pass that silently reinstalled on a heuristic would be a worse failure
than the one it names — the same discipline this service already applies to a
[dangling install record](../PostgresSchemaArchitecture).

**The denominator is printed.** A sweep that reports zero problems must say how many things it looked
at, so "nothing is wrong" and "nothing was checked" cannot read the same line.

## What this does NOT cover

Stated so nobody reads a green sweep as more than it is.

- **A node the package never declared.** The comparison is `declared − present`. Extra nodes in a
  partition are normal (satellites, instances, user content) and are never reported.
- **A node that is present but WRONG.** Completeness is about presence. Content drift is the
  `ModuleVersion` hash's job, and a node a user has claimed (`SyncBehavior != Include`) is
  deliberately theirs.
- **A record with no file map.** `Undeclared`, and it says so — but it cannot name what is missing,
  because nothing declared it.
- **A partial storage failure.** `ReadMany` reports absence by omission, so a silent partial loss
  reads as `Incomplete` rather than `NotObserved`. That errs toward reinstalling, which is
  idempotent — the safe direction.
- **The install's own postcondition.** The installer still does not read back what it wrote before
  stamping the record; the sweep catches it on the next boot instead. Closing that would move the
  check inside the install transaction and is a separate change.
- **A portal with very many partitions.** Every top-level node is a partition, and on a portal with
  many users that set is dominated by user roots this arm cannot be about. Above 2 000 unaccounted
  top-level partitions the abandoned-root arm **declines and says so** — one `NotObserved` line
  naming the count. It never reads the first N silently: the roots it skipped would then be spelled
  exactly like roots that are fine, which is the failure this whole page is about.

## Related

- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why the observed side is a batched
  read and never a query.
- [Roll Selection](../RollSelection) — completeness at the RELEASE scope: does a candidate ship all
  of an environment's plugins. It measures sealed bundles per package id and knows nothing about
  nodes, which is why it could not decline the release that #3485's instance failed to build.
- [Release Availability Gates](../ReleaseGates) — the same distinction one level up.
- [Data Access Patterns](../DataAccessPatterns) — the read/write surfaces this check composes from.
