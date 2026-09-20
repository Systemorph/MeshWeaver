---
Name: Install Completeness
Category: Architecture
Description: >-
  An install that half-lands must not read as a complete one. What the install record DECLARES landed,
  compared against what is actually in the mesh — the comparison nothing made until #3485, the five
  verdicts it can reach, and why only one of them is a pass.
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
`PackageInstaller.NodePathForFile`, the installer's OWN file→node rule.

🚨 **That rule has TWO halves, and counting only the first is #3659.** A file is not a node
candidate when it is a non-node file **by design** (`README.md` at the repo root, the
`manifest.lock` sidecar, a `content/**` asset) **or** when **no registered parser claims its
extension** — which is how a package's ordinary carry-along files are skipped by the install
without a word. Until #3659 the declared side asked only the first question while
`ParseCanonical` asked both, so the two disagreed about the very population this page compares:

```text
Chess ships 37 files; gen-manifests leaves manifest.lock out of the map  →  36 declared files
   content/og-card.png excluded (a content asset)                        →  35 "declared nodes"
   Chess/gui/rn/chess.tsx  →  no parser claims .tsx  →  no install ever writes it
sweep, every pod, every boot:  Chess → 1 of 35 declared node(s) are ABSENT: [Chess/gui/rn/chess]
                    after the fix:  36 file(s) declared, 2 not node files → 34 compared, Complete
```

Measured over `MeshWeaver.Plugins`' package folders on 2026-09-08: 617 `.cs`, 275 `.json` and 248
`.md` files a parser claims, against 200+ it does not — `.ts`, `.tsx`, `.py`, `.png`, `.mjs`,
`.js`, `.html`, `.gitignore`, `.mp4`, `.jpg`, and four files with no extension at all. Every one of
those was a permanent phantom ABSENT: reported at **Error**, indefinitely, spelled exactly like the
genuinely lost source node this whole page exists to find — and driving `SkipOrHeal` to run a FULL
reinstall of the package on every catalog visit that could never make the count reach zero.

`NodePathForFile` now takes the parser registry the install itself uses, and `ParseCanonical` is
expressed in terms of it, so there is ONE predicate rather than two implementations of half of one.
The registry is a required argument, deliberately: which extensions are claimed is DI-dependent (a
module contributes parsers), so a hard-coded list would be a fourth copy of a rule that drifts the
moment one is added.

### The content half: the question a PATH cannot answer

The predicate above unified the half a path can decide. **It structurally cannot decide the other
half**, and that residual was reachable, unguarded, and produced exactly the same symptom.

A file whose extension IS claimed can still fail to become a node on its CONTENT. `JsonFileParser`
answers "no node here" for a well-formed `.json` carrying no `$type`/`id`/`nodeType` — which is
every `tsconfig.json`, `package.json` and `app.json` that has ever sat inside a package folder, and
`Chess` ships exactly such a folder (`Chess/gui/rn/`). The installer writes nothing for it. The
sweep, deriving its population from paths, counted `{Pkg}/tsconfig` as a node the install owed the
mesh and reported it **ABSENT, at Error, on every boot of every pod, forever** — and no reinstall
could ever clear it, because the same bytes fail the same way. The line's own remedy
(*"reinstalling it now repairs it"*) is false for precisely this class, and it is spelled
identically to the genuinely lost node the sweep exists to find.

🚨 **The fix is evidence, not a longer exclusion list.** A list would have needed a case for `.tsx`,
then one for `package.json`, then one for the next shape — which is how this defect was filed
twice. Only the installer holds the bytes, so **the installer records the answer** and the sweep
reads it:

| Side | What it does |
|---|---|
| write | `ParseAll` already knew which node candidates did not parse; that set is now stamped on the record as `PackageManifest.UnreadableFiles` |
| declared | `DeclaredNodePaths` subtracts exactly those files — no list, no heuristic, no second derivation |
| report | they are named on their **own** line, at Error, as a PACKAGING defect that no reinstall can fix |

🚨 **The report names the FILE, never the node path it would have produced.** The remedy is applied
to `gui/rn/tsconfig.json`; a line naming `gui/rn/tsconfig` points at nothing on disk, and two files
can fold onto one node path so the node form can also name fewer things than there are. A report
whose purpose is to be acted on has to name the thing the operator acts on.

🚨 **`null` and empty are different answers, and only a WHOLE-PACKAGE look may produce the empty
one.** `null` means no install has recorded an answer — a record stamped before the field existed, or
one whose only writes since were partial — and subtracts **nothing**, so such a record behaves exactly
as it did before rather than reading as a clean zero. An empty set is the positive claim that every
declared node candidate was parsed and all of them became nodes.

🚨 **An update MERGES rather than replaces — and may not certify what it did not read.** An
incremental install examines only the files it fetched, so replacing would forget every unreadable
file outside the delta and the next boot sweep would start reporting them ABSENT again — this defect,
recreated inside its own bookkeeping. `PackageInstaller.MergeUnreadableFiles` keeps four rules: a file
this install examined is decided by this install (a package that fixes its file stops being reported),
a file it did not examine keeps the previous verdict, a file that has left the package is dropped
whatever it said, and — with no previous answer to build on — a pass that did not look at every
declared file returns `null` rather than an empty set. Two fetched files out of two hundred cannot
certify the other one hundred and ninety-eight.

That last rule also settles an ambiguity at the call site: the install record read there degrades a
FAULT to `null`, indistinguishable from "no record". A full install after such a fault re-derives the
whole answer and is correct regardless; an incremental one now yields `null` — honestly unknown —
instead of dropping every carried-forward entry.

🚨 **The buckets must ADD UP, and the order they are asked in decides whether they do.** The recorded
set is an install-time observation; the registry serving *this* boot may differ, because a module
contributes parsers. So the CURRENT rule is asked first — a file `NodePathForFile` maps to null today
is a non-node file today — and the record only classifies files that are still node candidates.
Asking the record first made such a file neither a non-node nor an unreadable one, and `DeclaredFiles`
silently stopped accounting for it.

🚨 **The incremental restore set excludes them too.** A file the install could not read as a node is
permanently node-less, so widening the fetch for it would re-fetch, re-parse and re-skip it on every
update forever, under a line calling it an absent node being restored — a second place where the
unhealable case wears the actionable one's words. A file whose hash *moved* is in the delta and
travels regardless, so a package that fixes its file is still re-examined and drops out of the record.

**Excluding it from the ABSENT count is only half the fix.** A file a package ships that cannot
become a node IS a fault — the package declares a node that will never exist — so silently dropping
it would trade a wrong Error for a missing one. What changes is *which sentence it gets*.

Measured 2026-09-16 over all 60 `manifest.lock`s in `MeshWeaver.Plugins` — 5,427 declared files, 998
node candidates, 259 of them `.json` — that set is currently **empty**. It is empty by luck, not by
construction: nothing before this change would have failed when it stopped being. The count now
travels on every verdict line, so an occupied case announces itself instead of arriving as an
unhealable ABSENT.

### The verdict states its own population

🚨 **A count over the wrong population reads exactly like a correct one.** Nothing in the old
output said which set had been counted, which is why a phantom ABSENT and a real one were
indistinguishable for as long as the sweep existed. Every verdict now carries and prints:

| Field | Says |
|---|---|
| `DeclaredFiles` | how many FILES the record's map holds — the population that was read |
| `NonNodeFiles` | how many of them are not node candidates, and why (README/manifest/content asset, module source, or an extension no parser claims) |
| `UnreadablePaths` | how many are a claimed extension the install met and could NOT read as a node — a packaging defect, counted separately because "excluded by design" and "the install failed on this file" must never render alike |
| `Declared` | the DISTINCT node paths the rest map to |

`Population` renders the three as one clause on every line that reports a verdict. The three are
printed rather than two and a subtraction because `DeclaredFiles − NonNodeFiles` legitimately
exceeds `Declared` when two files fold onto one node (the `X.json` → `X/index.json` layout move),
and a reader has to be able to see that rather than infer it.

A record whose files are ALL carry-along assets now reads `Undeclared` — *"all N file(s) the record
declares are non-node files … so this package declares no node to compare against"* — never
`Complete` (which would be the zero-found-zero-expected pass this page rejects) and never
`Incomplete` (which is what it answered before).

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

### The verdict names the RECORD it was taken over

A population without a record is half a denominator. The sweep's declared side comes from the
install record, and on 2026-09-12 a production line read

> `[InstallCompleteness] Store → 'Store': 2 of 201 declared node(s) are ABSENT.`
> `Missing: [Store/CoverActionDispatch, Store/Plugin/Test/CoverActionIdentityTests].`
> `Counted over: 201 file(s) declared, 0 of them not node files … → 201 distinct node path(s) compared.`

Reconciling that `201` against the record took a version-by-version read of `Plugins/Store` and a
commit-by-commit count of the package's source folder. The answer: the two record versions
straddling the sweep — v31 (2026-09-11T21:30:55Z) and v32 (2026-09-12T02:00:24Z), whose file maps
are byte-identical — declare **193** files and neither of those names, while a source snapshot with
exactly 201 node-candidate files **including both** existed at 2026-09-11T21:18:59Z. The count was
right about something; nothing in the line could say what.

🚨 **That is the same defect as a missing denominator.** A count taken over the current record and a
count taken over a stale one render identically. And the record is precisely the half that can be
stale: `InstallCompleteness.Observe` keeps the OBSERVED side off a query on purpose — *"never a
query … a stale negative here would manufacture a shortfall"* — while the DECLARED side arrives
through `InstalledPackageRepairService.InstalledRecords`, an eventually-consistent `GetQuery`.

So every verdict now carries `RecordIdentity` — path, node **version**, when that version was
written, and the manifest stamps that name the source snapshot behind the map
(`InstallCompleteness.DescribeRecord`) — and every line prints it:

```
Counted over: 201 file(s) declared, … , taken over Plugins/Store v32 (written 2026-09-12T02:00:24Z;
version 1.9.16, moduleVersion b53d4f05232ce57f, installedAtUtc 2026-09-10T12:55:28Z,
201 file(s) in the map)
```

Naming it does not make the read fresh — it makes a stale one **detectable**, and it puts the map's
own size beside the count it produced, so a record that has gone backwards is visible in the line
rather than only in an archaeology of its versions.

🚨 Every arm carries it, not just the shortfall: a `NotObserved` nobody can attribute to a record
version is as unactionable as an `Incomplete` nobody can attribute. And where a caller supplies
none, `Provenance` says *"the install record was NOT identified"* — never a blank that reads like an
identified one, for the same reason `NotObserved` is not a pass.

`EveryVerdictNamesTheRecordVersionItWasTakenOver` pins both directions.

## Where the verdict is consumed

### The install gate — it heals

`CatalogLayoutAreas.InstallOrUpdateCore` still compares the module hash first (it is cheap and it is
the right question about the source), but the skip now requires a **positive observation**:

| Verdict | What happens |
|---|---|
| `Complete` | skip — logged at Information with `{Present}/{Declared}` |
| `Incomplete` | **do not skip.** The full install runs and heals it — `DecideAndWrite` writes whenever `current is null` — and the shortfall is named at **Error** with the missing paths |
| `TornDown` | **do not skip** either — a lane that reaches this exit is *asserting* the package (the boot baseline, an environment flag, a human's Install or Update click), so a partition somebody deleted after the install is reinstalled in full, and the line says that is what happened. Only the boot repair pass treats this verdict differently (below) |
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

**It reports, and it hands a *repairable* shortfall to the install lane** (MeshWeaver#4812). Healing
still belongs to the install lane, which refuses to skip an incomplete package — but until #4812
nothing made that lane RUN for the package. The funnel only runs for a package some lane visits:
the platform baseline and the environment's flags on every boot, the operator's seed on its first
boot, the update reconciler only when the module hash *moved* (an equal hash returns before the
funnel), and a human's click. A package installed by hand from a source that has not changed since
was visited by nothing, so the sweep's own line — "Reinstalling it now repairs it" — described a
click nobody made, at Error, on every boot. Now the pass finishes by handing every `Incomplete`
record that is repairable to `InstanceAutoRegistrationService.ReassertInstalled`: the boot
install's own machinery (configured sources at their proven ref, the ownership holds, `RunAsSystem`,
the declared-access re-assert) over an explicit set of installed packages, **sequenced after the
default install's `Completed`** so two unattended passes never write one partition at once. It is
a heal, never an update: a package is re-asserted only where the source still serves the module
version its record carries, so the funnel can only skip or heal. A moved hash is an update and
stays with the package's own policy (the reconciler applies `Auto`; a human's click applies the
rest — both restore absent declared nodes as they go, #4259); a package no source lists any more
cannot be re-fetched and is named as an orphan for the admin list.

**Repairable means the install left a trace and part of it is gone.** Two shapes are deliberately
not repaired by a boot pass, and each is named on its own line:

- `TornDown` — see the next section. A reinstall would resurrect what an operator removed.
- An `Incomplete` verdict with *nothing* declared present, not even the root — the install left no
  trace: a deletion, a partition that is gone (the #3451 residue), or a read that answered nothing.
  None of those is a loss to restore from a boot pass. A record with no module identity is not
  repairable either: without one, "the source still serves what was installed" cannot be
  established, and a re-fetch would land the source's current tip.

A boot pass that reinstalled on a heuristic would still be a worse failure than the one it names —
the same discipline this service applies to a
[dangling install record](../PostgresSchemaArchitecture). What changed is that a positive
observation of a live install missing part of itself is not a heuristic.

**The denominator is printed.** A sweep that reports zero problems must say how many things it looked
at, so "nothing is wrong" and "nothing was checked" cannot read the same line.

### A record that outlived its partition — `TornDown`

`Plugins/ClaimsDeepfield` on memex.meshweaver.cloud was installed 2026-08-27T19:48Z (83 declared
nodes). The module was retired from its repository on 09-04 and the Space deleted on 09-05T09:03Z —
ten days before the record-follows-partition handler (#3451) existed, so the record survived.
Thirteen minutes later the boot pass re-asserted the record's declared access; that write is
create-only and *created* the root and its `_Policy` (`createdBy: system-security`, 09:16:50Z).
From then on the partition read as present, the #3451 gone-conjunction could never fire, and the
sweep counted *82 of 83 declared node(s) are ABSENT — reinstalling it now repairs it* at Error on
every boot: a recommendation to resurrect a retired module, folded into one incident for three
weeks (MeshWeaver#4812).

The count cannot tell that shape from a real loss. The stamps can: the partition **root is younger
than the install that declared it**, and **nothing else the install wrote is there**. That is the
`TornDown` verdict, computed purely in `InstallCompleteness.Compare` from the root's `createdDate`
(read in the same batched `ReadMany`, whether or not the record declares the root) against the
record's `installedAtUtc`. Both stamps have to be known; an unknown on either side leaves the
ordinary `Incomplete`, which errs toward reporting a loss and never toward inventing a deletion. A
young root with surviving content beside it (#638's lost-root-row shape) is `Incomplete` too — the
install left a trace, so it is a loss.

The sweep reports `TornDown` at **Warning** — the #3451 dangling-record level, one step on — with
the remedy: remove the record from *Catalog → orphaned install records*, or install the package
again deliberately. The boot repair pass never reinstalls it and never deletes the record. A lane
that asserts the package (the install gate above) reinstalls it in full, because whoever deleted
the partition, that caller wants the package installed.

## What this does NOT cover

Stated so nobody reads a green sweep as more than it is.

- **A node the package never declared.** The comparison is `declared − present`. Extra nodes in a
  partition are normal (satellites, instances, user content) and are never reported.
- **A node that is present but WRONG.** Completeness is about presence. Content drift is the
  `ModuleVersion` hash's job, and a node a user has claimed (`SyncBehavior != Include`) is
  deliberately theirs.
- **A record with no file map.** `Undeclared`, and it says so — but it cannot name what is missing,
  because nothing declared it.
- **A declared file whose CONTENT is not a node** — a well-formed `.json` with no
  `$type`/`id`/`nodeType`, or a file no parser can read. The extension gate cannot see it (the
  declared side holds paths, not bytes) so it is still counted. Empty over every package folder in
  `MeshWeaver.Plugins` as of 2026-09-08; stated because it is reachable, not because it is
  occupied.
- **A partial storage failure.** `ReadMany` reports absence by omission, so a silent partial loss
  reads as `Incomplete` rather than `NotObserved`. That errs toward reinstalling, which is
  idempotent — the safe direction.
- **The install's own postcondition.** The installer still does not read back what it wrote before
  stamping the record; the sweep catches it on the next boot instead. Closing that would move the
  check inside the install transaction and is a separate change.
- **A package whose source moved since the install.** The boot repair pass heals only at the
  recorded module version. Where the source now serves a newer build, the shortfall is named and
  left to the package's update policy or a human's Update click — both of which restore absent
  declared nodes (#4259). A `Notify` package that also lost nodes therefore stays short until
  someone clicks, and the pass says so.
- **Which of a partition's nodes an operator removed on purpose.** A live install missing part of
  itself is re-asserted; a node deleted by hand inside an installed package comes back on the next
  boot, exactly as it would for a baseline package. A deleted *partition* is `TornDown` and is not.
- **A portal with very many partitions.** Every top-level node is a partition, and on a portal with
  many users that set is dominated by user roots this arm cannot be about. Above 2 000 unaccounted
  top-level partitions the abandoned-root arm **declines and says so** — one `NotObserved` line
  naming the count. It never reads the first N silently: the roots it skipped would then be spelled
  exactly like roots that are fine, which is the failure this whole page is about.

## Package README pages and Git updates

A package installer sees repo-relative `Store/README.md` and writes `Store/README`.
Git sync sees that same file as `README.md` after selecting the `Store` subdirectory.
These paths must agree when the package's root `manifest.lock` declares the README in its
repo-relative `files` map. Git sync then imports it as a node, including when the node is missing.
A later package manifest that retires the file retains ordinary mirror deletion behavior.

Without a package manifest, the root README is a generated repository landing page, not a node.
Skipping that display file must not make an existing README node a deletion candidate. Export
also preserves an authored README instead of appending another generated file with the same path.
An unreadable package manifest fails the import before it can make this ownership decision.

The distinction matters when diagnosing a repair: a Git update activity is not evidence that
`PackageInstaller` performed a full reinstall. In #3686 the cited update activity explicitly fetched
Git deltas; the package reinstall path restored the README in a local real-mesh reproduction, while
the Git sync full-import reproduction deleted it before this correction.

## Related

- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why the observed side is a batched
  read and never a query.
- [Roll Selection](../RollSelection) — completeness at the RELEASE scope: does a candidate ship all
  of an environment's plugins. It measures sealed bundles per package id and knows nothing about
  nodes, which is why it could not decline the release that #3485's instance failed to build.
- [Release Availability Gates](../ReleaseGates) — the same distinction one level up.
- [Data Access Patterns](../DataAccessPatterns) — the read/write surfaces this check composes from.
