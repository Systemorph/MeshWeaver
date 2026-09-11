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

## Which workflow proves a repository — the declaration, and what happens when it is missing

The `workflow_run` payload carries two facts that are easy to confuse. `event` is **how** the run
started; `path` is **what** ran. Keying the publish signal on the first is a proxy, and the proxy
came apart in both directions:

- A `*/10` cron called **Auto-update green armed PRs** — present in Reinsurance, SocialMedia and Crm,
  and whose whole body is `gh pr list` → `gh api -X PUT …/update-branch` — checks out nothing and
  builds nothing. Measured over 48 h to 2026-09-10T19:36Z it recorded **20** build completions at
  `MeshWeaver.Reinsurance@7d29a303` and **40+** at `MeshWeaver.SocialMedia@07cc155e`, both commits
  whose own content CI had **failed**.
- Narrowing the trigger set does not close it. On core `192a60d84a36`, `push Chart Gate` and
  `push Hosting Operator` both concluded `success` in the second that
  `push MeshWeaver Build and Test` **failed**.
- And narrowing has already misfired the other way: the pre-2026-09-02 `event == "push"` test
  discarded Reinsurance's three green `repository_dispatch` runs and left `Underwriting/_GitSync`
  **38 h** behind a merged `main` with every delivery answering 200 OK
  (`Systemorph/MeshWeaver.Plugins#1194`).

So the trigger allow-list stays exactly as wide as it was — it answers a different question, and
narrowing it is what caused #1194 — and a third, independent guard answers the one that matters.

**Re-measured 2026-09-11, and the premise needed correcting.** #3978 stated that every repository's
content CI fires on `push` *and* `repository_dispatch` *and* `schedule`. Over the eight live
repositories (the ninth record, `Systemorph/education`, is a stale pre-rename alias last written
2026-08-14):

| repository | content CI | `push` | `repository_dispatch` | `schedule` |
|---|---|:--:|:--:|:--:|
| MeshWeaver.Plugins · .Education · .Reinsurance · .SocialMedia · .Manufacturing · .Crm | `ci.yml` | ✅ | ✅ | ✅ |
| Systemorph/MeshWeaver | `dotnet-test.yml` | ✅ | ✗ | ✗ (`merge_group`, `workflow_dispatch`) |
| Systemorph/Memex | `build.yml` | ✅ | ✗ | ✗ (`pull_request`, `workflow_dispatch`) |

Six of eight fire on all three; core and Memex publish on `push` alone. That changes nothing about
keying on the path — the two guards are orthogonal — but it does mean the allow-list is carrying
`repository_dispatch` and `schedule` **for the six satellites specifically**, which is exactly the
population #1194 was measured on. Dropping either would take their release-follow and cron signals
away again.

### Where the declaration lives

`GitHubWebhookProcessor.ContentWorkflowFor` resolves one repository's content-CI path, **and says
where that answer came from**, in this order:

| source | what declares it | why |
|---|---|---|
| `Configured` | `GitHub:ContentWorkflows:Repositories` — `{ Repository: "owner/repo", Path: ".github/workflows/x.yml" }` | a deployment's own escape hatch for a repository the platform does not know |
| `Platform` | the fleet's own table: `Systemorph/MeshWeaver` → `dotnet-test.yml`, `Systemorph/Memex` → `build.yml` | the platform ships the declaration, so it needs no portal's settings edited |
| `Convention` | `.github/workflows/ci.yml` | every node repo, enforced by the shared lane's `check-content-ci-path.py` |

**It is a fact about the REPOSITORY, not about one Space's source.** A field on `GitHubSyncConfig`
would be the wrong home: several Spaces sync the same repository, and they must not be able to
disagree about what proved it — 34 sources on memex.meshweaver.cloud point at MeshWeaver.Plugins
alone. The declaration therefore sits beside the platform, next to the `Admin/_Build/{owner}.{repo}`
record it governs, and is keyed by `owner/repo`.

**Why `Platform` exists rather than "convention plus config".** Measured 2026-09-11, with the
denominator: **nine** repositories hold an `Admin/_Build/{owner}.{repo}` record — the same nine on
memex.meshweaver.cloud and on memex.systemorph.com, both listings `truncated:false`. Eight satisfy
the convention or core's exception. The ninth, `Systemorph/Memex`, has **no `ci.yml` at all** (its
workflows are `build.yml`, `config-key-coverage.yml`, `deploy-drift.yml`, `helm-release.yml`,
`image-pins.yml`, `smoke.yml`), and two portals sync its `mesh/Deployments` tree. On the convention
alone it would have frozen on both, silently, the moment this shipped — #1194 recreated by the change
meant to prevent it. A configuration override would have fixed it only on portals whose settings
somebody edited; a platform declaration ships with the platform.

### 🚨 What happens when the declaration is absent or wrong

This is the whole risk of a fail-closed gate, and it is why the refusal is **reported**. A repository
whose content CI is somewhere the platform does not expect has every delivery refused, GitHub
answered 200, and every Space that syncs it silently stops advancing. #1194's entire cost was that
nothing said so.

The refusal is therefore classified, not just taken:

| the run's path | the repository | reported at |
|---|---|---|
| absent from the payload | any | **Warning** — the payload did not say what ran; that is the absence of evidence, not a verdict on it |
| not the expected path | nothing syncs it | Debug — no Space can be frozen, so the refusal cost nothing |
| not the expected path | synced, and a run at the expected path **has** been accepted before | Debug — routine: some other workflow finished green |
| not the expected path | synced, and **no** run at the expected path has ever been accepted | **Warning** — `I COULD NOT DETERMINE THIS REPOSITORY'S CONTENT CI` |

The Warning names both paths, the count and names of the frozen Spaces, that they are frozen and that
GitHub is answered 200, and the configuration section that fixes it. It **self-clears**: the
repository's next genuine content-CI run records `BuildCompletion.WorkflowPath`, and every later
refusal drops to Debug.

The conditioning is load-bearing rather than tidy. An unconditional Warning would fire on roughly
200 of core's refusals a day plus 144 from the PR-updater cron in each of three satellites, and
burying the one line that matters is the same failure as not emitting it. The precedent is
`ConfigsTargeting`'s zero-match Warning, which reports a delivery that matches no sync config for the
same reason and with the same reasoning.

**The residual, stated rather than implied.** A repository that has already published and *then*
moves its content CI keeps a record whose path still equals the expected one, so this stays quiet.
That case is covered a layer up instead: the shared `node-repo-validate` lane runs
`check-content-ci-path.py` against every satellite's tree, so moving the file reds that repository's
own CI, and `CoreContentCiRemainsAtThePublishSignalPath` does the same for core. Neither covers a
repository that calls neither — which is exactly why the two such repositories in the fleet are
`Platform`-declared rather than left to the convention.

### The build record says what proved it

`BuildCompletion` already carried `WorkflowName`; it now also carries **`WorkflowPath`**. The name is
a display string a maintainer can change in a one-line diff, and measured 2026-09-11 no consumer ever
keyed on it — its two readers are a log line in `PluginUpdateWatcher` and the identity comparison in
`MissedBuildFact`. The path is the identity the webhook actually admitted the run on, so recording it
makes the record self-describing, and it is what makes "this repository's content CI has never been
seen" an answerable question rather than a time-based guess. A record written before #3978 carries
`null`; the repository's next content-CI run fills it in.

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
