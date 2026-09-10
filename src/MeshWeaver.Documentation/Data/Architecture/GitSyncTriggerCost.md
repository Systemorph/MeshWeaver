---
Name: What a Green Build Costs a Synced Space
Category: Architecture
Description: A green build of a repository fans out to every Space that syncs it, and for two years exactly one field decided whether a delivery was free or a full clone. That field is deliberately frozen whenever an import does not fully converge, so a source that could not converge paid the full fetch on every delivery at the source repository's CI cadence. The fix is a second, weaker pointer — and the reason it needs a FINAL verdict rather than merely a recorded one.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v6"/><path d="M12 15v6"/><circle cx="12" cy="12" r="3"/><path d="M4.2 6.6l2.8 2.2"/><path d="M17 15.2l2.8 2.2"/><path d="M4.2 17.4l2.8-2.2"/><path d="M17 8.8l2.8-2.2"/></svg>
---

# What a Green Build Costs a Synced Space

[GitHub Sync](/Doc/Architecture/GitHubSync) keeps a Space current **without polling**: a green
build of the repository is a webhook delivery, and the delivery imports. That page describes the
feature. This page describes what one delivery **costs**, and the one field that decides it.

The short version:

> A delivery was free only when the source's `lastSyncCommitSha` already equalled the built commit.
> That field is held back — on purpose — whenever an import does not fully converge. So the
> cheapness gate and the convergence guard were **the same field**, and a source that could never
> converge was the source that re-cloned its repository most often.
>
> §7 is the fix: a **second, weaker pointer** — *we have already looked at exactly these bytes* —
> that never touches the conservative baseline. Its whole difficulty is that today's
> re-clone-per-delivery is *also* the retry loop for transient failures, so the new skip has to
> distinguish a verdict re-running cannot change from one that might.

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

That invariant is the design. Section 4 is about the sources for which it was **false**, and §7
about what makes it true again. 🚨 **Note what the fix is not: the trigger list is deliberately
unchanged.** Dropping `schedule` would take out 87 of these 254 deliveries at a stroke — and it
would be the same mistake the 2026-09-02 measurement corrected in the other direction, because a
cron run is the only green verdict some repositories ever produce, and for a *converging* source
those 87 deliveries already cost nothing. The trigger was never the defect.

## 2. The one gate that makes a delivery free

`GitHubWebhookProcessor.SkipReason` is the complete list of reasons a source that *targets* the
built repository is nevertheless left alone:

| Skip reason | Meaning |
|---|---|
| `config content could not be read` | the `_GitSync` node's content did not deserialize |
| `direction is ExportOnly` | the source refuses imports |
| `branch 'X' != built branch 'Y'` | the build is not on the branch this source tracks |
| **`already at this commit`** | **`lastSyncCommitSha == headSha`** |
| **`already attempted at this commit with a final verdict`** | **`lastAttemptedCommitSha == headSha` AND `lastAttemptWasFinal`** (§7) |

Only the last two are a *work* saving. Until the second one existed the saving was keyed on exactly
one field, so deliveries two and three of a merge, and every cron tick until the next merge, were
free **iff** the first delivery advanced `lastSyncCommitSha` to that commit — and §4 is why, for
some sources, it never did.

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

🚨 **None of the three clauses may be relaxed** — each is an incident, and the failure mode of
relaxing one is silent data loss or silently-missing content, not a slow sync. The field stays
exactly as conservative as it is; §7 adds a *different* field for the *different* question.

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

> ⚠️ **`MayAdvanceBaseline` and `StaticRepoImportResult.Converged` are not the same predicate**, and
> the difference is `PruneRefused`. A run whose source listing came back truncated (#3589 / #3614)
> is *not* `Converged` — the marker refuses to record it as such — but `MayAdvanceBaseline` does not
> read that field, so such a run **does** advance `lastSyncCommitSha` and the source is skipped by
> §2's first arm on every later delivery of that commit. That is pre-existing behaviour, deliberately
> untouched here: closing it would make a source pay *more* clones, which is the opposite of this
> page's subject. It is noted because a reader comparing the two predicates will otherwise assume
> one of them is a typo.

### Reading it off a live portal, read-only

**The discriminator is `lastSyncedAt` far behind `lastSyncAttemptAt` with an outcome of
`Imported`.** That pair says: a sync ran, it reported that it imported, and it still did not
reconcile — which is `MayAdvanceBaseline` returning false, which holds `lastSyncCommitSha` too.

> ⚠️ **Two things that look like this and are not.** A frozen `lastSyncedAt` beside outcome
> `Skipped` is the sanctioned no-op (#677) — see *The four facts* in
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

Since §7 there is a third pair of fields to read, and for a settled source they are the ones that
explain the frozen dates: `lastAttemptedCommitSha` (what the last attempt *looked at*) and
`lastAttemptWasFinal` (whether looking again could change anything). The settings tab renders the
second as *"this commit has a final verdict — the next new commit re-attempts"*, so a reader is not
left to infer a stopped webhook from a stopped clock.

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
that really is a content tree, or delete the source. The engine's own share of the problem was the
one section 4 states — that the cheapness gate had no way to say *"we have already looked at exactly
these bytes for this source"* independently of *"the mesh is known to hold this commit"*. §7 is that
sentence, given a field. It makes a misconfigured source cost **one** clone per new commit instead
of one per delivery; it does not make the source correct, and the configuration is still the fix.

## 7. The fix: two questions, two fields

The engine had one field answering two questions that must be allowed to diverge:

| Question | Who asks it | Field |
|---|---|---|
| *Is the mesh known to hold this commit's content?* | the two-way conflict guard, the diff base, the "up to date?" display | `lastSyncCommitSha` — **unchanged**, still held by `MayAdvanceBaseline` |
| *Have we already looked at exactly these bytes?* | the webhook's cheapness gate | `lastAttemptedCommitSha` + `lastAttemptWasFinal` — **new** |

`RecordSyncResult` writes the new pair on **every import conclusion**, in the same `stream.Update`
patch as the recency stamp. `SkipReason` gains a fifth arm that fires only when *both* halves agree
with the built commit. Nothing else moved: the baseline stays as conservative as #675 / #677 /
#2229 item C need it to be, the next NEW commit still brings the source a full unscoped import, and
the GUI's own **Update to latest** never consults `SkipReason` at all.

### 🚨 Why "already attempted" is not enough on its own

**Today's re-clone-per-delivery is also, accidentally, the retry loop** for exactly the failures the
importer refuses to call final. `IsContentVerdict` is a short allow-list on purpose — `InvalidPath`,
`InvalidNodeType`, `ValidationFailed`, `Unauthorized` — and everything else stays retryable, because
getting *that* backwards freezes a healthy partition out of the mesh (#3101). A store that was
briefly unreachable comes back as `Unknown` ("Persistence read failed…", "Inner CreateNode
faulted…"); the next delivery is the only thing that retries it, and on a quiet repository the next
commit is never.

So a skip keyed on the sha alone would trade one bug for a worse one. The second half is
`StaticRepoImportResult.VerdictIsFinal` — *would running again change the answer?* — which is a
different question from `Converged` (*is the partition now equal to the source?*):

| Outcome shape | `Converged` | `VerdictIsFinal` | Next delivery at the same commit |
|---|---|---|---|
| clean `Imported` | ✅ | ✅ | skipped by §2's **first** arm (the baseline advanced) |
| `Preserved > 0`, nothing failed | ❌ | ✅ | **skipped by the new arm** — the same bytes meet the same server-newer nodes |
| `ImportedWithContentErrors` (every failure a content verdict) | ❌ | ✅ | **skipped by the new arm** — the same bytes break the same rules |
| `ImportedWithErrors` (one or more retryable failures) | ❌ | ❌ | **attempted** — a store blip may not recur |
| outcome `Failed` (the whole import faulted) | ❌ | ❌ | **attempted** — no per-file tally to classify, and unknown means retryable |
| `PruneRefused` (truncated listing) | ❌ | ❌ | attempted — though §4's aside notes the baseline advanced anyway |

`Preserved > 0` is the row that matters most in practice: it is the arm both measured live sources
sit on, and the one the import marker (§5) does nothing for. The preserve decision is taken against
`lastSyncedAt`, which no re-import moves — so a preserved node is preserved identically on every
pass until a person commits it back or discards it.

### What clears the licence

The pair is **written, never merged** — an older value must not survive a conclusion that set none,
or a skip would be licensed for an attempt that never happened. Three things clear it:

- **an export / commit-back**, because it advances the conflict horizon, which is what decides
  *which* live nodes an import preserves — so any earlier "final at commit X" is stale;
- **a hold** (the sealed-publication gate), because no attempt ran, and a seal arriving later is the
  archetype of a condition that clears without the commit moving;
- **a new commit**, trivially — the recorded sha stops matching.

🚨 **What does NOT clear it, stated rather than hidden:** a mesh-side change at an unchanged commit —
someone deleting the very node that was being preserved, say. The next green build still skips, and
the source waits for the repository's next commit or for a human pressing **Update to latest**. That
is the same contract `lastSyncCommitSha` has always had for the converging case, and it is the
deliberate price of not re-cloning a repository ten times an hour to discover that nothing changed.

### The controls

`GreenBuildSkipsASettledSourceTest` measures both directions **on the clone itself** — the transfer
is the cost, so an assertion on a decision function or on a log line would not be measuring it. The
GitHub transport and one marked storage path are substituted; the webhook processor, the sync
service, the importer and the node streams are real.

- *A content verdict at this commit costs no second clone, and a new commit still does import* — a
  nested `Space` refused with `InvalidPath`, then the same green build again (**no fetch**), then a
  new sha (**fetch, at the commit that build proved**). It also pins `lastSyncCommitSha` still
  `null`: the conservative baseline must not have moved.
- *A failure that might not recur is still attempted at the same commit* — one node whose store
  faults with the production connect-timeout shape, so the pass is `ImportedWithErrors` with
  `VerdictIsFinal == false`; the same green build then **must** reach GitHub. This is the control
  that could falsify the first: a fix that skips everything strands every self-healing source.

## 8. Why the cron probe stays in the allow-list

A third of core's deliveries come from one workflow that compiles nothing —
`.github/workflows/prod-synthetic-probe.yml`, `cron: "*/15 * * * *"`, whose entire body is `curl`
against the two live portals (no checkout, no restore, no compile, no artefact). Excluding it, or
dropping `schedule` from `PublishSignalTriggers` altogether, looks like the cheapest possible win.
It was measured, and it is not one.

**The window (24 h to 2026-09-10T17:35Z, core `main`, green): 329 runs enumerated against a
`total_count` of 329 — complete.** 254 are publish signals; 75 are `workflow_run`-triggered and
already rejected. `schedule` accounts for 112 of the 254 (44 %), the probe alone for 87 (34 %).

- **The probe publishes no commit that a push run did not already publish.** Its 87 runs cover 34
  distinct `head_sha`s, and every one of them also carried a green `push` run of the content CI.
  (One sha appeared to be probe-only until its push runs were found nine minutes before the window
  opened — a window artefact, not a case.) So the probe's deliveries are *duplicates*: for a
  converging source §2's first arm already answers them for free, and for a settled source §7's new
  arm now does. **Excluding it saves nothing the engine fix does not already save.**
- **No repository in the fleet depends on `schedule`.** All six satellites' content CI fires on
  `push` *and* `repository_dispatch` *and* `schedule`; Memex's has no cron at all. Counting shas
  whose only green content-CI run was a `schedule` run: 51 in the 30 days to 2026-09-10, **none on
  or after 2026-09-01** (the satellites moved from `17,47 * * * *` polling to a daily `03:xx` cron
  plus the `meshweaver-upstream-published` dispatch on 2026-08-29/30). Over the whole of September,
  removing `schedule` would have widened exactly one repository's worst quiet stretch, by six hours.
- **But it would not close the class.** A `push`-triggered run of a workflow that is *not* the
  content CI is admitted exactly as readily as a cron: on core `192a60d84a36`, `Chart Gate` and
  `Hosting Operator` both went green on `push` in the same second that `push MeshWeaver Build and
  Test` **failed**. Narrowing the trigger set would suppress the loudest member of the class and
  leave the rest — and it is the same shape of mistake as the pre-2026-09-02 `event == "push"` test,
  which discarded `Systemorph/MeshWeaver.Reinsurance`'s three green `repository_dispatch` runs and
  left `Underwriting/_GitSync` 38 h behind a merged main with every delivery answering 200 OK.

🚨 **And the class has a correctness half, not only a cost half.** `Auto-update green armed PRs`
(`cron: "*/10 * * * *"` in Reinsurance, SocialMedia and MeshWeaver.Crm) does nothing but call
`gh api … /update-branch` on armed PRs — it checks out nothing — and in the 48 h to
2026-09-10T19:36Z it recorded **20 green build completions for
`MeshWeaver.Reinsurance@7d29a303`** and **40+ for `MeshWeaver.SocialMedia@07cc155e`**, both of them
commits whose content CI had **failed**. That is a delivery authorising an import of a tree no build
proved — the proposition `SyncRefContract` exists to guarantee.

**The conclusion: the trigger event is a proxy for the wrong proposition.**
`PublishSignalTriggers` answers *"how was this run started"*; what the record needs is *"did this
repository's own CONTENT CI prove this tree"*. The discriminator for that is already in the payload —
`workflow_run.name` / `.path` — and every repository has exactly one such workflow (`ci.yml` in each
satellite, `dotnet-test.yml` in core). Keyed on that, the probe, the PR-updater, `Deployment Smoke`,
`Arm credential`, `Chart Gate` and `Hosting Operator` all fall out under one rule, no repository
loses its only signal, and the trigger allow-list can stay as wide as it is. That is a separate
change with its own risk — a repository that declares the wrong workflow stops syncing silently,
which is exactly MeshWeaver.Plugins#1194 recreated — and it is filed as #3978, together with the
correctness finding above.

## See also

- [Syncing a Space with GitHub](/Doc/Architecture/GitHubSync) — the feature, the webhook setup, and the four facts — the horizon, the baseline, the recency stamp, and §7's attempt pair.
- [The Import Marker Records Convergence](/Doc/Architecture/ImportMarkerRecordsConvergence) — the content-addressed short-circuit and what may write it.
- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — fingerprint, activity lock, upsert, prune.
- [Sync Ref Contract](/Doc/Architecture/SyncRefContract) — what a build completion records and who consumes it.
