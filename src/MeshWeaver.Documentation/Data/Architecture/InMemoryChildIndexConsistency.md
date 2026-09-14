---
nodeType: Markdown
name: In-Memory Child Index Consistency
category: Architecture
description: How the in-memory store's children index handed a synced source query a partial, permanent snapshot — the query half of the declined-bundle failure that held every plugin publication for two days — what separates it from the mid-install judgement race it composes with, and the three rules that now keep a reader from ever seeing a half-built index.
icon: /static/NodeTypeIcons/box.svg
---

# In-Memory Child Index Consistency

Between 2026-09-12T14:27Z and 2026-09-14 no plugin publication sealed and every portal stayed at
`627fb3cd` (MeshWeaver.Plugins#1823). The gate's stated failure was one NodeType failing its
compile on names it ships — `CS0246 'StoreTexts'`, `'OperationStep'`, `'OperationPlan'`, *"and 760
more"* — after its prebuilt bundle had been **declined** on a source-fingerprint mismatch
(MeshWeaver.Plugins#1827). This page records what the log actually measured, why the decline was
*false*, and the defect in `InMemoryStorageAdapter`'s children index that produced it.

It composes with [#4280](https://github.com/Systemorph/MeshWeaver/issues/4280) — the
`StaleAdopted` judgement being taken **mid-install** — and the two are stated side by side below,
because a fix for either one alone would not have cleared the run.

## What the red job measured

MeshWeaver.Plugins main run 34792068713 (attempt 2, `c3b0224c`), job 103825681160, platform set
`3.0.0-ci.8539`. The `Essentials` install and the one declined type, with timestamps:

| time | event |
|---|---|
| 01:09:22 | `Store` installed — 208 nodes written, incl. the 18 files of `Store/Core/Source` |
| 01:09:47 | `Store` **re-installed for idempotence: 0 written, 208 unchanged** — the installer READ every node back from the storage adapter to decide "unchanged", so all 18 were provably in the adapter's dictionary |
| 01:12:11.938 | `── Essentials: installing 33 file(s)…` |
| 01:12:12.529 | `Prebuilt assembly ADOPTED for Essentials/OperationRequest` |
| 01:12:12.543 | `[AdoptedSourceStamp] … the source moved past the adopted build (bundle d5fde5520ffd127d, live 69c30b59c87fd20f) … COMPATIBLE … a compile of the live source is dispatched` |
| 01:12:12.570 | `DECLINED before writing (#2813)` |
| 01:12:13.212 | compile 1 dispatched |
| 01:12:13.263 | `warmed installed root Essentials` — the install ends |
| 01:12:24.673 | compile 1 **fails** — `Matched Code nodes (39)` of 49 declared |
| 01:12:28.510 | compile 2 dispatched (the gate's second adoption attempt, declined the same way) |
| 01:12:33.987 | compile 2 **fails** — the SAME 39 nodes, the same missing names, byte for byte |
| 01:12:34.588 | `RED Essentials/OperationRequest: compile=FAILED(Error)` → `GATE FAILED` |

The 10 nodes the source query never returned, against the repository at `c3b0224c`:

| declared query | committed | matched |
|---|---|---|
| `Essentials/OperationRequest/Source` | 5 | 3 — missing `OperationDsl`, `OperationRequestControlPlane` |
| `Essentials/OperationRequest/Test` | 2 | 2 |
| `shared=@Store/Core/Source` | 18 | **10** — missing `ChromeLocale`, `CoverContract`, `NamespacePicker`, `NodeElement`, `PlatformModelPath`, `StoreTexts`, `TestsRun`, `VideoEmbed` |
| `shared=@Store/Licensing/Source` | 9 | 9 |
| `shared=@Store/Publishing/Source` | 11 | 11 |
| `shared=@Store/Coupon/Source` | 3 | 3 |
| `shared=@Store/Install/Source` | 1 | 1 |

Every missing name in the 760 diagnostics is declared by one of those 10 files. No size, header
(`// <meshweaver>`), or name pattern separates the missing eight from the matched ten. The walk
dropped no read — `grep -c "swallowed for path"` over the job is **0** — so the storage-adapter
query provider was handed a **short child list** for `Store/Core/Source`, and it was the same short
list twice, fifteen seconds after the Essentials install had ended.

Three facts carry the diagnosis:

1. **The eight Store nodes were in the store.** The idempotence re-install at 01:09:47 read them
   back. "Not landed yet" is excluded for the Store half — by 2.5 minutes.
2. **The answer was frozen.** Compile 2 ran on the same 39 nodes as compile 1. A synced query
   (`SyncedQueryMeshNodes`) seeds its snapshot from ONE `Initial` and then applies change events;
   a node written **before** the subscription never produces one, so a short `Initial` for a
   directory nobody writes to again is permanent for the life of the subscription — and the
   NodeType hub's sources watcher holds that subscription open.
3. **The fingerprint, the decline and both compiles read the same snapshot.** The
   `nodetype-sources:<path>` query is one process-wide cached observable
   (`NodeSources.GetSources`), shared by the sources watcher that computes
   `CurrentSourceFingerprint` and by the compile activity. `69c30b59c87fd20f` is the fold over the
   partial set; the bundle's `d5fde5520ffd127d` — baked in the same run from the same commit — is
   the fold over the whole set. The bundle was right. The decline was a measurement of the index,
   not of the sources.

## The mechanism

[#4169](https://github.com/Systemorph/MeshWeaver/pull/4169) (merged 2026-09-13T10:18Z — after the
last seal, inside both red platform sets, absent from the portals' `8411`) gave the in-memory
adapter a children index so the gate's subtree queries stop scanning every key. Its first shape:

```csharp
ChildrenOf: if (IndexedCount != _nodes.Count)
                lock (Rebuild) if (…) { Children.Clear(); foreach (k in _nodes.Keys) Index(k); IndexedCount = _nodes.Count; }
Added:      Index(path); IndexedCount = _nodes.Count;        // no lock
```

`IndexedCount == _nodes.Count` was used as "the index is consistent". A writer's `Added()` set it
**without the rebuild lock**, so a write landing while another reader was mid-rebuild made the flag
read true while `Children` was half-cleared. The next reader passed the outer check, skipped the
lock, and read whatever the rebuild had re-indexed so far — ten of the eighteen `Store/Core/Source`
entries. Rebuilds were frequent under a concurrent install: two writers can leave `IndexedCount`
stale (W1 reads the count before W2's add and stores it after), and every subsequent read then
rebuilt the whole index, `Clear()` first.

Two properties made the wrong answer permanent rather than transient: the synced query caches the
`Initial` (fact 2 above), and the directory it was wrong about was not being written to. A
transient index inconsistency became a frozen source set, and the frozen source set became a false
fingerprint, a false decline, two doomed compiles, a `PARKED` verdict and a red gate.

## Two halves, and why neither alone would have cleared the run

| | #4280 — judged mid-install | this page — a partial listing, frozen |
|---|---|---|
| where | `ApplyAdoptedSourceStamp` / `CompileWatcher` / the park registry | `InMemoryStorageAdapter`'s children index |
| what goes wrong | the fingerprint is compared 0.5–1.7 s into a package's install, before its own siblings have landed | the walk behind the source query is handed fewer children than the store holds, for a directory written minutes ago |
| how it ends | the compile fails on siblings that land seconds later; a LATER compile succeeds; the park keeps the first verdict | the compile fails on siblings that are already there; a later compile reads the same cached snapshot and fails identically |
| tell in the log | `Matched Code nodes` grows between compiles; recovery on a third compile | `Matched Code nodes` is identical across compiles after the install ended |

MeshWeaver.Reinsurance PR#209 (job 103902719770, 08:11Z, set `8547`) shows the first tell:
`Ifrs17/ReportingNode` matched 13 on compiles 1 and 2 (0.5 s apart) and **succeeded on compile 3**
after the idempotence re-install; `Reinsurance/AggregateExposureProfile` and
`Underwriting/AuthorityRung` likewise. That log does not separate the two mechanisms — every
missing name there belongs to the package being installed at that moment, and the CreateOrUpdate
reply precedes the debounced per-node persist, so "seven seconds after `Installed`" does not prove
a node was in the adapter when the walk ran. Its recovery on a third compile is what the Plugins
run never got. The Plugins run separates them: the missing nodes were three minutes old and proven
persisted, and the second compile changed nothing.

Fixing #4280 alone (defer the judgement while the set is short; do not park a compile whose inputs
were incomplete) would have left the Plugins run red: its second compile, fifteen seconds after the
install ended, still saw 39 of 49. Fixing the index alone would have left the Reinsurance run red
on the first judgement's park. Both are needed; they are one failure seen from two layers.

**Population, stated as the denominator:** every seeded-gate run the lane produced on 2026-09-14
on sets `8539+` — MeshWeaver.Plugins (one type), MeshWeaver.Reinsurance (three types, twice) — and
**zero** of the ~20 unseeded gate-shard runs on the same platform (`declined=[] parked=[]` on every
one). The unseeded gate compiles from a release request issued after the install; only the seeded
path judges a type's sources at the instant of adoption, half a second into its package's install,
while the index is under concurrent writes.

## What it is NOT

- **Not a type failing on its own declared `shared=@` sources.** The sources resolve; the index
  under the query did not list them. The bundle held the right bytes.
- **Not a framework-identity transition artifact.** The bundle was baked in the same run from the
  same commit; the identity does not enter a source fingerprint.
- **Not closed by `Modules__RequirePrebuilt=true` on the gate** (#4266, merged 2026-09-14T05:15Z).
  That change makes the seeded gate adopt a *compatible* bundle instead of compiling — which is
  right for the gate, and turns this shape green there. It does not touch the index, so an
  in-memory mesh that DOES compile (every `MonolithMeshTestBase` test, the tester's unseeded gate,
  a Monolith on `AddInMemoryPersistence`) kept the defect; and a gate that adopts on a false decline
  is reading a wrong fingerprint and drawing a right conclusion by luck of the module version.
- **Not on the portals.** They run Postgres; `PostgreSqlMeshQuery` answers scoped queries in one
  round trip and never touches this index.

## The three rules the index now keeps

The fix ([PR #4295](https://github.com/Systemorph/MeshWeaver/pull/4295)) rewrites the index's
concurrency contract in `InMemoryStorageAdapter`:

1. **The store and the index move together.** Every adapter write path mutates `_nodes` and the
   index in one `Mutate` section (microseconds of dictionary work — never a subscriber callback,
   never IO). `IndexedCount` is an exact tally, so `IndexedCount != _nodes.Count` means exactly one
   thing: the dictionary was mutated behind the adapters' back (a test seeding the map directly).
   That is what the count IS a guard for. It is NOT a guard for a writer that has not finished —
   there is no such window any more — and it is NOT a signal a reader may act on without the lock.
2. **A rebuild is copy-on-write.** It builds a fresh index and swaps the reference atomically; the
   live index is never cleared in place, so a reader holds a complete index at every instant. A
   reader that finds a refresh already in flight (`Monitor.TryEnter` fails) reads the live index
   and moves on. Only the very first build of a dictionary seeded before any adapter saw it is
   waited for — before it there is no index at all.
3. **A writer never waits for a rebuild.** The rebuild publishes the index it is building as
   `Pending` before it snapshots the keys, in one `Mutate` section, so every write either precedes
   the snapshot (and is in it) or observes `Pending` and indexes into both. The rebuild takes
   `Mutate` per key, so an install interleaves between keys instead of stalling behind a scan.

The pin is `InMemoryStorageAdapterChildIndexConcurrencyTest`: a rebuild is parked mid-flight
through the adapter's test seam (`OnRebuildBuilt`, a volatile `int` under a bounded
`SpinWait.SpinUntil`, released in a `finally`), an unrelated write lands while it is parked and must
complete, and a second reader asks about a directory whose nodes were all written before the
rebuild began. On the unfixed adapter it answered **0 of 18** — *"a partial listing returned as
complete"*. A load control (four writers, four readers, a 6,000-key store) guards the fixed
invariant; on the unfixed adapter that loop could not even reach the fast path on a laptop — the
reader reads `IndexedCount` before `_nodes.Count`, which takes every bucket lock, so under
hammering writers every check mismatched and rebuilt. The window is reachable where writes are
sparse relative to reads: one node per hub round trip, subtree queries walking between them, which
is the seeded install.

## Residue, named

- **Where the walk's answer is still not the store's.** A node whose CreateOrUpdate has been
  acknowledged but whose debounced per-node persist has not reached the adapter is invisible to a
  walk by construction; its change event follows and repairs a live snapshot. That is #4280's
  window, and it is closed by not judging or parking on a set that is still landing — the other
  half.
- **The portals stay held until a Roll.** Both `memex` and `memex-cloud` run set `3.0.0-ci.8411`
  (identity `sd608997…`) while publications now seal at `8539+` (`s0285c07…`); `Signature/_GitSync`
  on both reads `Held … 'plugins' is sealed at 627fb3cd` as of 2026-09-14T07:29Z. No gate fix moves
  that: it is a `Hosting/InstanceAction` Roll on the control instance to a set that carries this
  fix, which is the maintainer's decision.

## Related

- [Synced Mesh Node Queries](../SyncedMeshNodeQueries) — why one `Initial` seeds the snapshot and
  a node written before the subscription never repairs it
- [Query Provider Parity](../QueryProviderParity) — the in-memory evaluator and Postgres are two
  executors; this index is the in-memory one's
- [CI Content Bake](../CiContentBake) — the seeded gate, and what `Modules__RequirePrebuilt` does
  and does not decide
- [Bundle Delivery Stages](../BundleDeliveryStages) — the decline is at the *select* stage; the
  false fingerprint behind it is upstream of every stage
- [Change-Feed Isolation](../ChangeFeedIsolation) — the feed that would have carried the repair,
  had there been one
