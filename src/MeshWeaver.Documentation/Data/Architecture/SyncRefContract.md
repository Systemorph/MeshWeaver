---
Name: The Sync-Ref Contract
Category: Architecture
Description: An UNATTENDED import reads a commit CI proved green; only a human-initiated "Update to latest" may read a branch tip. Why resolving a ref twice put sources no build had compiled onto two production portals for five hours, where each import path now gets its ref, and which one still does not.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><line x1="12" y1="3" x2="12" y2="9"/><line x1="12" y1="15" x2="12" y2="21"/><circle cx="12" cy="3" r="1.5"/><circle cx="12" cy="21" r="1.5"/></svg>
---

# The Sync-Ref Contract

**An unattended import reads a commit a build PROVED. Only a human-initiated "Update to latest" may
read a branch tip.**

That is the whole rule. Everything below is why it has to be a rule rather than a habit, and what
enforces it.

"A build" means the repository's **content CI**, not any workflow that happened to finish green at
the same commit. The webhook verifies the stable workflow file path:
`.github/workflows/ci.yml` for content/node repositories, and core's established
`.github/workflows/dotnet-test.yml` exception. Trigger, branch and workflow identity are independent
guards. This matters in both directions: a scheduled PR updater once authorized a red Reinsurance
tree more than twenty times, while a green core Chart Gate could have masked a red build-and-test
run (#3978).

## The two refs, and why they are not the same ref

A GitSync'd Space is configured with a repository and a **branch**
(`GitHubSyncConfig.Branch`, default `main`). A branch is not a coordinate — it is a pointer that
moves. A **commit sha** is a coordinate.

Every import therefore has to answer "which tree?", and there are exactly two answers available:

| answer | resolved | correct for |
|---|---|---|
| the configured **branch** | inside the fetch, at whatever instant the fetch runs | a human clicking **Update to latest** — that is literally what they asked for |
| an explicit **commit sha** | before the trigger is even decided | every machine trigger |

The failure mode is not choosing the wrong one once. It is **resolving the ref twice**: deciding
*whether* to import against one ref and *what* to import against another. A trigger that filters its
candidates on a build's `head_sha` and then asks for "latest" has done exactly that, and the two
answers agree only while nothing merges in between.

## What it cost: Systemorph/MeshWeaver.Plugins#1430

Measured, on the two production portals, 2026-09-06.

```
22:17:21Z  MeshWeaver.Plugins#1413 merges to main — the Payments split. Its Store/* sources need
           IPaymentProvider (core) and the MeshWeaver.Payments.* module bundle.
22:38:18Z  A main run for 8d4920c93 — a commit that PREDATES #1413 — finishes.
           GitHub delivers workflow_run/completed/success for that run.
22:38:39Z  memex.systemorph.com  imports … main's HEAD, which is now past #1413.
22:39:32Z  memex.meshweaver.cloud imports … the same.
           Store/Catalog, Store/Order, Store/Plugin, Store/Maintenance → compile Error.
03:50Z     Still Error. version and lastModified have not moved in five hours: the compile result
           is latched and nothing re-tries it until the source changes — and the next sync would
           bring the SAME source.
07:14Z     A roll to a platform carrying IPaymentProvider + the Payments module clears it.
```

The instance's runtime could compile neither symbol. On the commercial portal that is the catalog,
the order/checkout path, the plugin page and the maintenance type: **paying customers could not buy
or install for roughly five hours.**

Note what the trigger was *not*. The run that fired the webhook was green, on the default branch,
for a commit whose tree compiled fine. Every guard in the publish path did its job. The sources that
landed were simply **not that tree** — no build had ever compiled them against anything.

### Why the sweep found it and nothing else did

`search 'nodeType:NodeType content.compilationStatus:Error'` returned five, against 195 reading
`Ok`. Nothing else was red: the webhook answered 200, the build record was written, the import
activity finished, CI on both repositories was green. A per-NodeType `Error` is not an alert.

## The contract, per path

| path | trigger | ref it reads | why |
|---|---|---|---|
| `GitHubActivityExtensions.UpdateToLatestFromGitHub` | a person clicks **Update to latest**, or the MCP `git_hub_sync` `update` verb | the configured **branch** | the human is asking for latest and is present for the result |
| `GitHubActivityExtensions.UpdateToProvenCommitFromGitHub` | `GitHubWebhookProcessor` on a green `workflow_run` | the run's **`head_sha`** | the tree CI proved, and the tree the candidates were selected against |
| `GitHubActivityExtensions.ReimportFromGitHub` | a person names a commit | that **commitish** | stated by the caller |
| `PluginUpdateWatcher` → `PackageUpdateReconciler` | the same green build, via the `BuildCompletion` node | `BuildCompletion.HeadSha` | already correct before #1430 — this is the path GitSync now agrees with |

Two consumers read the same fact — "repo X built green at sha Y". The package path always installed
at Y. The GitSync path recorded Y, filtered on Y, and then fetched the branch. Making them agree is
the fix; there was never a reason for them to differ.

### Why a sha is fetchable at all

The contract only works because the transport can serve one. `GitProtocolRepoClient.Fetch` runs
`init` + `fetch --depth 1 origin <commitish>` + `checkout FETCH_HEAD`, and git's `upload-pack`
**serves refs and FULL SHAs**. A `workflow_run` payload's `head_sha` is a full 40-hex sha, so it goes
over the wire like a branch name would. The one commitish the protocol cannot serve is an
ABBREVIATED sha, and that case already has a REST fallback in the same method. Nothing new was needed
here — the "re-import at a chosen commit" flow has always taken this path.

## No fallback — the third state is a refusal, not a default

`UpdateToProvenCommitFromGitHub` **throws** when it is handed an empty commit. That is deliberate and
it is the load-bearing half of the fix:

```csharp
if (string.IsNullOrWhiteSpace(commitSha))
    return Observable.Throw<string>(new ArgumentException(
        $"An unattended GitHub import of '{spacePath}' must name the commit a build proved; "
        + "there is no branch-HEAD fallback (MeshWeaver.Plugins#1430)."));
```

"We could not establish a proven commit" and "the branch tip" are different answers, and a fallback
collapses the first into the second — a value that was never read, presented as a real one. That is
the same shape as a CI gate whose input step carries `continue-on-error`, and it fails in the same
direction: silently, looking healthy. The webhook path already refuses a `workflow_run` payload
carrying no `head_sha`; this makes the refusal structural rather than a property of one caller.

The user-visible half is localized: the import's activity is titled
`activity.gitsync.updateToProvenCommit.title` and its progress line
`activity.gitsync.updateToProvenCommit.fetching`, both naming the short sha, in English and German.
An operator reading the Space's activity log can see *which commit* landed without leaving the page.

## What this does NOT fix, stated plainly

**Sources can still arrive that the instance's platform cannot compile.** Pinning the ref guarantees
the tree was proved green *by its own repository's CI, against that repository's platform pin*. It
does not guarantee the importing instance runs a platform new enough for it. Had #1413's own green
build been the trigger, the same four NodeTypes would have failed — later, and for a tree somebody
had at least compiled. That is a different question — *what a publication contains, and which
instances may adopt it* — tracked separately (MeshWeaver.Plugins#1438, MeshWeaver#3538). This page is
only about which commit's sources an instance receives.

**One unattended import still reads a branch tip.** `ModuleDiscoveryService.FirstImport` — the
AutoSync provisioning path — brings a newly created, still-empty Space to `main`. It is left as it is
on purpose: a consumer instance that never receives GitHub webhooks has no `BuildCompletion` record
to pin to, so refusing there would mean AutoSync provisioning simply never runs, and the blast radius
is a new empty partition rather than live content going dark. It is named here so it is a known
residue rather than an oversight.

**Out-of-order builds can move a Space backwards.** Two green builds of one branch can finish out of
order, so a later webhook may name an earlier commit — and it is imported, not skipped: GitHub's
compare answers `behind` when the recorded base is not an ancestor, `GetChangedPaths` returns `null`
for anything that is not `ahead`/`identical`, and a null diff means *full import* (never a silent
under-import). So the Space really does step back to the older tree.

That is stated rather than claimed away, and it is the price of the guarantee. What lands is still a
tree CI proved; the next green build brings the Space forward; and it is exactly the property the
package path has always had (`PluginUpdateWatcher` → `BuildCompletion.HeadSha`), so pinning the ref
makes the two agree rather than introducing a new risk. Resolving the branch instead trades a
recoverable step backwards for landing a tree *no build ever proved* — which is what five hours of
dark Store looked like.

## The seal's attribution marker: whose CONTENT, never whose LANE

`SealedSyncGate` holds a module-bearing repository's sources at the commit sealed for this
instance's framework identity. To do that it has to answer one question about each sealed source on
the shelf — *is this a publication of the repository whose green build just arrived?* — and it
answers it from `repository.txt`, a marker `publish-bake-bundles.sh` writes beside the bundles.

**That marker records the repository the baked CONTENT came from. It is not
`$GITHUB_REPOSITORY`** — and the difference is not academic, because it is exactly the case the
marker exists for. Core CD's `plugins-bake` job bakes **MeshWeaver.Plugins** content
(`content-repository: Systemorph/MeshWeaver.Plugins`) from a run whose `$GITHUB_REPOSITORY` is
`Systemorph/MeshWeaver`. Stamping the lane's own name therefore filed the `plugins` seal under the
platform — and `SealedSyncGate.BelongsTo` takes the marker branch whenever the marker is non-empty
and never falls back to commit attribution, so a Plugins green build found *no* seal attributable to
Plugins, `mine` came back empty, and `Decide` returned `Go`. **The gate was inert for the one
repository it was written for**, while MeshWeaver.Plugins' own `publish-bake` — the second writer of
that same prefix — stamped it correctly. Two writers, two different answers, one directory
(MeshWeaver#3583).

The value is now passed in as `BAKE_CONTENT_REPOSITORY`, from the same
`inputs.content-repository || github.repository` expression the content CHECKOUT resolves — one
answer to "whose content is this", used by both — and both live publishers set it explicitly, so
nothing depends on the script's fallback. The fallback to `$GITHUB_REPOSITORY` remains only for a
lane whose workflow copy has not been re-pinned yet, where the lane's own name is the whole answer
the old script had.

🚨 **This is the same trap one field over from the content sha**, which the bake lane already warns
about in its own words — *"The CONTENT commit, not `$GITHUB_SHA`. They differ exactly when
`content-repository` is set (the platform baking Plugins)."* Anything the publication records about
*what was baked* is a fact about the content, and `github.*` describes the lane.

## How to check it is still true

- `test/MeshWeaver.Hosting.Test/BuildTriggeredSyncPinsTheBuiltCommitTest.cs` pins both halves: the
  green build imports at the payload's sha, and the unattended import refuses an empty one. It
  asserts on the ref that reaches `IGitHubRepoClient.Fetch` — not on a log line and not on a decision
  function — because the defect was precisely that the decision and the fetch disagreed.
- On a live portal, the import's own activity names the commit; the sync source records it as
  `lastSyncCommitSha`, which is now the same value the webhook filtered on, so
  `GitHubWebhookProcessor.SkipReason`'s "already at this commit" compares like with like.
- After any wave that moves plugin sources, the readiness sweep is one call:
  `search 'nodeType:NodeType content.compilationStatus:Error'`. A `searched: false` envelope is a
  FAILED sweep, not a clean one.
- `.github/scripts/test-publish-bake-overlap.py` EXECUTES the publish script and reads the marker off
  the resulting bytes, in both directions: a bake of another repository's content records that
  repository, and a lane baking its own content still records itself. Run it against the pre-fix
  script (`--script <copy>`) and the first case goes red — which is how the attribution defect above
  was falsified rather than assumed.
- On the shelf: `<publishedRoot>/<live framework identity>/plugins/repository.txt`. If it reads
  `Systemorph/MeshWeaver`, that seal predates the fix and the gate cannot attribute it to Plugins.

## Related

- [Node Type Compilation](../NodeTypeCompilation) — why a latched compile `Error` does not retry.
- [Module Versioning](../ModuleVersioning) — what an instance's module set is and how it is pinned.
- [Continuous Delivery Contract](../ContinuousDeliveryContract) — the publication registration the
  incident's timeline runs through.
- [The Cross-Repo Pair Gate](../CrossRepoPairGate) — the neighbouring family: a core half landing before
  the half that depends on it.
