---
Name: What a Green Build Costs a Synced Space
Category: Architecture
Description: A green build of a repository fans out to every Space that syncs it, and exactly one field decides whether a delivery is free or a full clone. That field is deliberately frozen whenever an import does not fully converge — so a source that cannot converge pays the full fetch on every delivery, and its retry cadence is set by the source repository's CI schedule rather than by anything about the source.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v6"/><path d="M12 15v6"/><circle cx="12" cy="12" r="3"/><path d="M4.2 6.6l2.8 2.2"/><path d="M17 15.2l2.8 2.2"/><path d="M4.2 17.4l2.8-2.2"/><path d="M17 8.8l2.8-2.2"/></svg>
---

# What a Green Build Costs a Synced Space

[GitHub Sync](/Doc/Architecture/GitHubSync) keeps a Space current **without polling**: a green
build of the repository is a webhook delivery, and the delivery imports. That page describes the
feature. This page describes what one delivery **costs**, and the one field that decides it.

The short version:

> A delivery is free only when the source's `lastSyncCommitSha` already equals the built commit.
> That field is held back — on purpose — whenever an import does not fully converge. So the
> cheapness gate and the convergence guard are **the same field**, and a source that can never
> converge is the source that re-clones its repository most often.

## 1. What actually fires — a green build is not a merge

`GitHubWebhookProcessor` acts on a `workflow_run` delivery when **both** guards pass: the run's
`conclusion` is `success` **and** its `head_branch` is the repository's default branch, **and** the
run's own trigger is in the publish-signal allow-list — `push`, `repository_dispatch`, `schedule`,
`workflow_dispatch`. (A run triggered *by another workflow* — `event: workflow_run` — is not a
publish signal and is ignored.) Every accepted delivery rewrites
`Admin/_Build/{owner}.{repo}` ([`BuildCompletion`](/Doc/Architecture/SyncRefContract)) and then fans
out to every sync source of that repository.

**One merge is not one delivery.** Measured on `Systemorph/MeshWeaver` for the 24 h ending
2026-09-10T17:35Z — 254 green publish-signal runs on `main`:

| Workflow | Trigger | Runs |
|---|---|---:|
| Prod synthetic probe | `schedule` | 87 |
| Chart Gate | `push` | 47 |
| Hosting Operator | `push` | 47 |
| MeshWeaver Build and Test | `push` | 46 |
| Continuous Delivery (main) | `schedule` | 19 |
| Arm credential / Shared rule blocks / Pinned image digests / Lock pinned image digests / Homebrew | `schedule`, `push` | 8 |

`Admin/_Build/Systemorph.MeshWeaver` on `memex.meshweaver.cloud` read **version 4778** at
2026-09-10T17:21Z, first written 2026-08-06T07:38:37Z — **4,775 recorded deliveries in 35 days**,
256 of them in the last 24 h (10.7/h, one every 5.6 minutes). The version series matches the run
census above, which is what makes the model checkable rather than inferred.

Two consequences follow, and both are load-bearing:

- **Three workflows go green on the same commit**, so a single merge produces roughly three
  deliveries carrying the *same* `head_sha`.
- **A third of the deliveries are a cron, and it builds nothing.** `Prod synthetic probe`
  (`.github/workflows/prod-synthetic-probe.yml`) is `cron: "*/15 * * * *"` — it makes HTTP requests
  to the live portals and asserts on the answers. It produces no artefact and touches no tree. But
  it is a `schedule` run that goes green on the default branch, so it is a publish signal, and its
  `head_sha` is whatever `main`'s tip happens to be — unchanged between merges.

For a healthy source both are harmless, and the allow-list says so explicitly
(`PublishSignalTriggers`):

> **Widening this cannot cause churn.** A sync source already sitting on the built sha is skipped by
> `SkipReason` ("already at this commit"), so a scheduled or dispatched re-verification of an
> unchanged default branch triggers no import at all.

That invariant is the design. Section 4 is about the sources for which it is **false**.

## 2. The one gate that makes a delivery free

`GitHubWebhookProcessor.SkipReason` is the complete list of reasons a source that *targets* the
built repository is nevertheless left alone:

| Skip reason | Meaning |
|---|---|
| `config content could not be read` | the `_GitSync` node's content did not deserialize |
| `direction is ExportOnly` | the source refuses imports |
| `branch 'X' != built branch 'Y'` | the build is not on the branch this source tracks |
| **`already at this commit`** | **`lastSyncCommitSha == headSha`** |

Only the last one is a *work* saving, and it is keyed on exactly one field. Deliveries two and three
of a merge, and every cron tick until the next merge, are free **iff** the first delivery advanced
`lastSyncCommitSha` to that commit.

## 3. What a non-skipped delivery costs

`GitHubSyncService.FetchAndImport` runs in this order:

```text
repoClient.Fetch(repoUrl, commitish, subdirectory, token)   ← git init + fetch --depth 1 + checkout
      ↓                                                       into a fresh temp dir, then read the
ParseSnapshot(snapshot, …)                                    whole worktree into RepoFiles
      ↓                                                     ← every file materialised as a MeshNode
repoClient.GetChangedPaths(baseSha …)                       ← the git diff — only now
      ↓
StaticRepoImporter.ImportSource(…, changedNodePaths)        ← the diff scopes THIS
```

🚨 **The git-diff scope is a WRITE scope, never a fetch scope.** It exists to stop a routine push
re-materialising a whole partition into the live compiler (the memex-cloud loop of 2026-07-23), and
it does that. It does not, and cannot, avoid the transfer: the diff is computed against
`snapshot.CommitSha`, which only exists once the snapshot has been fetched. So **fetch + parse is
the floor cost of any delivery that is not skipped** — and for a source with no `subdirectory`, that
is the entire repository, every time.

`IGitHubRepoClient` in production is `GitProtocolRepoClient`, so this is a real clone over the git
protocol into a unique temp directory, not a REST tree walk:

```text
git init -q
git fetch -q --depth 1 origin {commitish}
git checkout -q --detach FETCH_HEAD
ReadWorktree(tmp, prefix, pathFilter)          ← the subdirectory is applied HERE
```

🚨 **A `subdirectory` scopes what is PARSED, never what is TRANSFERRED.** There is no
sparse-checkout and no partial-clone filter: the shallow pack is one exchange for the whole tree, and
the prefix is a filter over the worktree afterwards (`GitProtocolRepoClient.Fetch`, "*the git
protocol transfers the (shallow) pack in one exchange, so the filter is applied while reading the
worktree*"). A source watching a four-file subdirectory therefore clones the entire repository on
every non-skipped delivery, exactly like a source watching the root. What the subdirectory does save
is the parse and the write fan-out — which is why a source with **no** subdirectory on a large repo
is the expensive shape: every file in the repository becomes a `MeshNode` in memory.

## 4. 🚨 The freeze: three ways `lastSyncCommitSha` stops advancing

`GitHubSyncService.MayAdvanceBaseline` is the whole decision, and it is **correct**:

```csharp
result.Preserved == 0
  && result.Failed == 0
  && !string.Equals(result.Outcome, "Failed", StringComparison.OrdinalIgnoreCase)
```

Each clause is there because advancing past it lost data or lost content:

| Clause | Why it holds the baseline |
|---|---|
| `Preserved > 0` | a two-way import kept server-newer nodes, so the mesh is AHEAD of the repo. Advancing makes the next diff overwrite the very edits two-way just protected (#675 / #677). |
| `Failed > 0` | some node did not land. Advancing makes the miss PERMANENT — every later delivery answers `already at this commit` while the node is still absent (#2229 item C). |
| `Outcome == "Failed"` | the whole import failed and carries no per-file tally to read. |

Now put that beside section 2. **The gate that makes a delivery free and the guard that protects
un-landed content are reading the same field**, so holding the field for the second reason
necessarily disarms the first:

> A source whose import does not fully converge pays a **full fetch + parse on every delivery**,
> for as long as it does not converge — and the rate is the **source repository's CI cadence**, not
> anything about the source. A cron probe that never changes the tip re-clones the repository just
> as hard as a merge does.

So the allow-list's stated invariant — *"a sync source already sitting on the built sha is skipped …
so a scheduled re-verification triggers no import at all"* — holds for every source that converges
and **for no source that does not**. On the converging sources the 15-minute probe costs nothing at
all; on one that cannot converge, the same probe is a full clone of that repository — 87 times in the
measured 24 h (the cron offers 96; only the green runs are publish signals) — on top of roughly three
per merge.

None of the three freeze conditions resolves by itself:

- a **preserved** node stays preserved until someone commits it back or discards it;
- a node **failed** on a content rule fails identically on the same bytes for ever;
- an import that failed outright fails again until its cause is fixed.

### Reading it off a live portal, read-only

**The discriminator is `lastSyncedAt` far behind `lastSyncAttemptAt` with an outcome of
`Imported`.** That pair says: a sync ran, it reported that it imported, and it still did not
reconcile — which is `MayAdvanceBaseline` returning false, which holds `lastSyncCommitSha` too.

> ⚠️ **Two things that look like this and are not.** A frozen `lastSyncedAt` beside outcome
> `Skipped` is the sanctioned no-op (#677) — see *The three clocks* in
> [GitHub Sync](/Doc/Architecture/GitHubSync). And a `lastSyncCommitSha` a few hours behind the
> branch tip is normal: the webhook imports **at the commit the build proved**, not at the tip, so
> the recorded sha is the last delivered green commit, not `HEAD`.

Measured across all 67 readable `_GitSync` nodes on `memex.meshweaver.cloud` (search returned 67,
`truncated:false`; 67 read, 0 unreadable), 2026-09-10T17:30–17:39Z:

| Source | Repo / subdir | `lastSyncedAt` | `lastSyncAttemptAt` | Outcome | Gap |
|---|---|---|---|---|---|
| `Essentials/_GitSync` | Plugins / `Essentials` | 2026-08-10T07:23:48Z | 2026-09-10T16:10:01Z | `Imported` | **31 d** |
| `Deployments/_GitSync` | Memex / `mesh/Deployments` | 2026-09-09T01:10:46Z | 2026-09-10T15:27:00Z | `Imported` | 1 d 14 h |

`Essentials` sits on `lastSyncCommitSha: 709e7307…`, **436 commits behind** the repository's head,
with at least 11 unimported files under `Essentials/`. It is the **only** one of the 34 sources on
that repository off the shared baseline: its 33 siblings — `Store`, `Publish`, `Hosting`, `Chess`,
`BusinessRules`, … — all carried `f4570459…` with `lastSyncedAt` **equal to** `lastSyncAttemptAt`.
Same repository, same webhook, same schedule; the difference is convergence.

`Deployments/_GitSync` is the only source in the set carrying `twoWay: true`, i.e. the
`Preserved > 0` arm — and the **independent** node of the same path on `memex.systemorph.com` reads
`lastSyncedAt: 2026-08-19T20:06:37Z` against a `lastSyncAttemptAt` of 2026-09-10T15:27:00Z: **22
days** of attempts that never reconciled. (The two portals hold two independent nodes here, not a
replica and its lag — read both before concluding anything about either.)

### The check

1. `get @{Space}/_GitSync` and compare `lastSyncedAt` against `lastSyncAttemptAt`. Equal (or within
   the same run) ⇒ converging, nothing to see. A gap of days with outcome `Imported` ⇒ frozen.
2. Only then is the sha worth looking at — it tells you how much the freeze has cost:

```bash
gh api "repos/<owner>/<repo>/compare/<lastSyncCommitSha>...main" \
  --jq '{ahead:.ahead_by, behind:.behind_by, files:(.files|length)}'
```

`behind_by: 0` confirms the recorded sha is still an ancestor (nothing was force-pushed away);
`ahead_by` is how many commits of deliveries the source has been paying for without converging.

## 5. What the content-addressed marker does and does not close

[The import marker](/Doc/Architecture/ImportMarkerRecordsConvergence) narrows this a long way. When
a pass refuses nodes and **every** refusal is a verdict about the content, the importer records
`ImportedWithContentErrors`, stamps the fingerprint marker `Failed`, and the next pass at the same
fingerprint short-circuits to `Skipped` — which carries `Failed == 0`, so the baseline finally
advances and section 2's gate re-arms. That turns "every delivery" into "twice per new commit".

Two limits are worth stating exactly:

- **It engages after the fetch.** The marker id *is* the fingerprint of the parsed source, so the
  repository has already been cloned and parsed by the time the short-circuit is reachable. The
  saving is the write fan-out and the NodeType recompiles, not the transfer.
- **It needs the pass to be ALL-deterministic.** `IsContentVerdict` is a deliberately short
  allow-list — `InvalidPath`, `InvalidNodeType`, `ValidationFailed`, `Unauthorized` — and everything
  else, including every unclassified infrastructure fault (`Unknown`: "Persistence read failed…",
  "Inner CreateNode faulted…"), stays retryable, because getting *that* backwards freezes a healthy
  partition out of the mesh (#3101). One `Unknown` among four hundred content verdicts keeps the
  outcome `ImportedWithErrors`, keeps the marker `Warning`, and keeps the partition re-importing on
  every delivery.

## 6. The worst case is a misconfigured source

A `_GitSync` is Space-scoped: its repository (or its `subdirectory`) is expected to hold that
Space's **content tree**. Point one at a repository whose tree is *code* — a framework repo with no
`subdirectory`, say — and the import can never come out clean:

- a `samples/**` tree of sample partitions imports as nodes *under* the Space, and a partition-owning
  type refused there answers `A 'Space' owns its partition, so it must be top-level` (`InvalidPath`);
- content whose `$type` no registry resolves is refused by `ContentDiscriminatorValidator`
  (`ValidationFailed`);
- a file that maps to neither a NodeType nor content is refused as a bare node (`ValidationFailed`).

Every one of those refusals is permanent, so the source never converges, so — by section 4 — it pays
the full clone on **every** delivery of that repository, for ever. `Systemorph/Memex#237` is the
live instance: a `MeshWeaver/_GitSync` on `memex.meshweaver.cloud` pointing at
`https://github.com/Systemorph/MeshWeaver` with no subdirectory, 389 refused nodes per pass.

**The fix for that shape is the configuration, not the engine**: give the source a `subdirectory`
that really is a content tree, or delete the source. The engine's own share of the problem is the
one section 4 states — that the cheapness gate has no way to say *"we have already looked at exactly
these bytes for this source"* independently of *"the mesh is known to hold this commit"*.

## See also

- [Syncing a Space with GitHub](/Doc/Architecture/GitHubSync) — the feature, the three clocks, the webhook setup.
- [The Import Marker Records Convergence](/Doc/Architecture/ImportMarkerRecordsConvergence) — the content-addressed short-circuit and what may write it.
- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — fingerprint, activity lock, upsert, prune.
- [Sync Ref Contract](/Doc/Architecture/SyncRefContract) — what a build completion records and who consumes it.
