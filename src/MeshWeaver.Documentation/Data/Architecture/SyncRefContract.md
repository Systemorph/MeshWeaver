---
Name: The Sync-Ref Contract
Category: Architecture
Description: An import of a repository whose bundles this instance runs lands on the commit those bundles were baked from — whoever asked, a person included (since 2026-09-17). Every other repository keeps the older rule — an unattended import reads a commit CI proved, a person may read a branch tip. Why resolving a ref twice put sources no build had compiled onto two production portals for five hours, and where each import path gets its ref.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><line x1="12" y1="3" x2="12" y2="9"/><line x1="12" y1="15" x2="12" y2="21"/><circle cx="12" cy="3" r="1.5"/><circle cx="12" cy="21" r="1.5"/></svg>
---

# The Sync-Ref Contract

> 🚨 **Superseded in part by policy [`module-sync-per-manifest-hash`](../PolicyNotProse)** — see
> [Module Sync Per Manifest Hash](../ModuleSyncPerManifestHash). **The seal no longer chooses, redirects
> or holds a source's commit.** A green build imports the commit it built. A person's Update or
> Re-import reads exactly what was asked. A first import (discovery, boot install) resolves the
> configured branch, and the seal's arrival imports nothing by itself. Each module of the tree is then
> judged alone by the content hash in its `manifest.lock`, and the seal decides only whether each
> NodeType adopts prebuilt bytes or compiles. What still holds from this page: an unattended import
> reads a commit a build PROVED when a build names one, and "a build" means the repository's content
> CI. The sections below that say "lands on the sealed commit" or "holds" describe the rule this
> policy replaced, and are kept because they are why it was replaced.

**The rule this page stated until then:** an import never put a repository's sources ahead of the
bytes this instance ran. When a publication of the repository was sealed for this instance's
framework identity, EVERY import of it — a green build, the seal's own arrival, a first import, the
boot install, *and a person pressing **Update to latest** or **Re-import at this commit*** — landed on
the commit that publication was baked from, or imported nothing and said why. For any other
repository, an unattended import read a commit a build PROVED, and a person could still read a branch
tip.

That was the whole rule. Everything below is why it has to be a rule rather than a habit, what
enforces it, and — because it once read differently — what changed on 2026-09-17 and what that costs.

> 🚨 **Until 2026-09-17 this page said the opposite about people:** *"Only a human-initiated 'Update
> to latest' may read a branch tip."* That exception is withdrawn (MeshWeaver#3845 hole 3, maintainer
> decision). The section *The human exception, withdrawn* below says why, and names the consequence
> an operator meets first: **recovery from a hold is what the hold names — roll the instance, or fix
> the publishing lane — never a tip import.**

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
| the configured **branch** | inside the fetch, at whatever instant the fetch runs | a person clicking **Update to latest** on a repository this instance runs **no** publication of — that is literally what they asked for |
| an explicit **commit sha** | before the trigger is even decided | every machine trigger, and **every** import of a repository whose publication is sealed for this instance |

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
| `GitHubActivityExtensions.UpdateToLatestFromGitHub` | a person clicks **Update to latest** (`GitHubActionArea`), the MCP `git_hub_sync` `update` verb, or a first import of an unattributable repository | the commit **sealed** for this identity when the repository is attributable (**held** while that seal is torn, at an unknown commit, or disagreeing); the configured **branch** otherwise | `SealedSyncGate.DecideRequestedImport` — MeshWeaver#3845 hole 3, below |
| `GitHubActivityExtensions.UpdateToProvenCommitFromGitHub` | `GitHubWebhookProcessor` on a green `workflow_run` | the run's **`head_sha`** when it is sealed for this identity or the repository is unattributable; otherwise the commit **sealed** for this identity (**held** while that seal is unusable) | `SealedSyncGate.DecideBuild` — the webhook lane, below |
| `GitHubActivityExtensions.ReimportFromGitHub` | a person names a commit or a branch (`GitHubSyncSettingsTab` → **Re-import at this commit**) | the same as **Update to latest**, with the typed **commitish** in place of the branch | `SealedSyncGate.DecideRequestedImport` |
| `PluginUpdateWatcher` → `PackageUpdateReconciler` | the same green build, via the `BuildCompletion` node | `BuildCompletion.HeadSha` | already correct before #1430 — this is the path GitSync now agrees with |
| `ModuleDiscoveryService.FirstImport` | AutoSync provisions a new Space (boot, or a green build) | the commit **sealed** for this identity; the branch only for a repository this instance runs no publication of | `SealedSyncGate.DecideFirstImport` — MeshWeaver#3845 hole 2, below |
| `InstanceAutoRegistrationService.InstallDefaults` — the **boot default install** (`preInstalled`, `InstallByDefault`, feature flags) | every boot, after the bake settles | the commit **sealed** for this identity; `PluginCatalog:Sources:N:Ref` only for a repository this instance runs no publication of; **held** while that repository's seal is torn | the same `DecideFirstImport`, since MeshWeaver#4259 — see "The lane the closure missed", below |

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

**The AutoSync first import is closed — by a pin the residue's own rationale overlooked.** (This
paragraph once opened with "the last unattended branch-tip import is closed". It was not the last;
the boot default install was still one, and the section after this one is what it cost.)
`ModuleDiscoveryService.FirstImport` — the AutoSync provisioning path — used to bring a newly created,
still-empty Space to `main`. It was left that way on purpose, and the reasoning was sound as far as it
went: *a consumer instance that never receives GitHub webhooks has no `BuildCompletion` record to pin
to, so refusing there would mean AutoSync provisioning simply never runs.*

What that argument missed is that `BuildCompletion` is not the only commit a build proved. Such a
consumer **does** hold one, on its own disk and with no webhook involved: the `source-commit.txt`
marker beside the bundles **sealed for its own framework identity**. That commit is strictly better
evidence than a branch tip — it names a tree CI proved *and* the tree the bytes this instance actually
runs were baked from. So the first import now asks `SealedSyncGate.DecideFirstImport`, and there are
three answers:

| what the seal says | what the first import does |
|---|---|
| no publication of this repository is sealed for this identity | resolve the branch — *the residue's case, unchanged* |
| exactly one sealed commit, attributed by the `repository.txt` marker | `UpdateToProvenCommitFromGitHub` at that commit |
| attributable but torn, at an unknown commit, or two seals disagreeing | **import nothing**, say why, retry next scan |

The original rationale stays intact where it applied: a repository this instance runs no publication
of still provisions at the tip, so AutoSync on a webhook-less consumer is exactly as alive as before —
that *was* the case the residue was about. The blast radius of a hold is still "a new empty
partition", never live content going dark.

🚨 **How a hold releases, stated exactly — the loose version of this is wrong and was caught in
review.** Two paths re-run a held first import: the discovery scan's next pass (a `_GitSync` with no
`LastSyncCommitSha` is `ModuleDiscoveryService.Evaluate`'s re-import trigger) and
`SealedPublicationSyncReconciler`, which brings a source onto the sealed commit when the seal is read.
But scans are enqueued at **boot** and on **`BuildCompletion`** emissions, and the reconciler runs from
`ShippedPrebuiltBundles.SeedPublishedRoot`, which is **boot-time** — so on an instance receiving no
build webhooks, *both reduce to the next process start*, and a seal that completes mid-process is not
noticed until then. That is [#4063](https://github.com/Systemorph/MeshWeaver/issues/4063)'s shape and
this hold inherits it. The Warning the scan logs says so in those terms rather than "later", so an
operator reading it knows which event to wait for.

🚨 A first import can be attributed **only by the repository marker**. It has neither a built commit
nor a last-sync commit, so both commit-attribution legs of `SealedSyncGate.BelongsTo` have nothing to
compare against — a seal predating that marker therefore reads as unattributable and keeps today's
behaviour. Unattributable is never a guess in either direction. `MeshWeaver#3845` hole 2.

## The lane the closure missed: the boot default install (MeshWeaver#4259)

The table above had one unattended importer missing from it, and it was the one that runs on
**every boot of every deployment**: `InstanceAutoRegistrationService.InstallDefaults`, which installs
the `preInstalled` baseline, the operator's `InstallByDefault` patterns and the environment's
feature-flag packages. It listed each configured source at `PluginCatalog:Sources:N:Ref` — `main` on
every fleet record — resolved at fetch time, and stamped `installedFromRef: main` on the install
record. That is the shape this page exists to remove, and nothing on this page named it.

**Why it mattered is that a partition can have two unattended writers.** On memex.meshweaver.cloud
the `Hosting` partition is written by both `Hosting/_GitSync` (MeshWeaver.Plugins, `subdirectory:
Hosting`, created 2026-08-19T10:43Z by AutoSync) and the `Plugins/Hosting` install record (created
2026-08-19T14:27Z, three hours later, by the boot install of `preInstall: Plugins/*`).
`ModuleDiscoveryService` steps over a module that already has an install record; the installer had
no reciprocal rule, so both stayed. Once `SealedSyncGate` landed, the two disagreed about the tree:

```
2026-09-13
08:09:48Z  Hosting/_GitSync imports at 627fb3cd — the Plugins commit SEALED for framework
           sd608997 (the image's 2026-09-12 build). lastSyncCommitSha = 627fb3cd.
08:12:26Z  The boot install lists Plugins@main → Hosting 1.19.0 (a 2026-09-13 tree: the Triage
           types), writes eleven Source/Test nodes 627fb3cd does not have, stamps the record
           v93/v94 with installedFromRef: main.
08:12:35Z  Hosting/TriageStatus compiles Succeeded from those nodes (its Release node names them).
21:55:55Z  Next boot. The bake declines the Hosting bundles on their source fingerprint — the
           live sources are 1.19.0's, the bundles were baked from 627fb3cd — so
           SealedSyncReconcile answers ReconcileAtSealedCommit ("the live sources have drifted
           from the commit they claim; re-importing at it"): the sync rewrites
           PlatformBuildInboxWatcher and DeploymentTestsArea to 627fb3cd's content and PRUNES the
           eleven nodes. lastSyncedAt = 21:56:05.97Z, same commit.
21:56:07Z  Hosting/TriageStatus: MISSING SOURCES 2 of 2 → compilationStatus: Error.
22:03:03Z  The boot install lists Plugins@main again → 1.19.16, diffs it against ITS OWN record
           (1.19.0): two changed files. The pruned eleven are declared unchanged, so they are not
           fetched. Record v95, installedFromRef: main. Eight Hosting NodeTypes stay at Error.
```

Every reading is from `get_versions` on the two records and the Release nodes; the sealed commit's
tree was confirmed in the repository (`627fb3cd` carries no `Triage*` file and its
`PlatformBuildInboxWatcher.cs` does not reference `TriageIntake`, which is why the 21:58:01Z compile
of `Hosting/Deployment` succeeded from thirteen sources and the 22:03Z one failed from fourteen).
Note what each half did *correctly*: the sync obeyed the seal, and the reconciler's "drifted from
the commit they claim" was a true statement. The drift was the other writer.
[Declared Is Not Landed](../DeclaredIsNotLanded) closed the *persistence* half — an update now
restores a declared node that is absent — and on its own that would have turned this into an
every-boot flap: sync prunes, install restores, bake declines, sync prunes.

🚨 **That flap is not hypothetical — it ran for eleven boots on a second partition, and it read as a
self-heal.** Measured on memex.meshweaver.cloud 2026-09-16 (core `c84c6c05`, which predates this
section's fix): `Feedback/_GitSync` (MeshWeaver.Plugins, `subdirectory: Feedback`) imports at
`627fb3cd`, whose `Feedback/` tree has no `Source/FeedbackHandover.cs`; `Plugins/Feedback` 1.0.16
(module `caffd87567c65b4f`, `installedFromRef: main`) declares it. The package's module hash never
moved, so every boot took the hash-EQUAL exit, whose completeness check (MeshWeaver#3485) named the
node ABSENT and re-installed it:

```
2026-09-16
21:31:58Z  Feedback/_GitSync  Imported @ 627fb3cd          (v501; again 21:32:01Z, v502)
21:33:16Z  boot install: "Package Feedback … 1 of 14 declared node(s) are ABSENT:
           [Feedback/Feedback/Source/FeedbackHandover] … being REPAIRED"
21:33:24Z  Plugins/Feedback record v28, installedFromRef: main   — the node is written back
21:54:40Z  Feedback/_GitSync  Imported @ 627fb3cd          (v503) — and pruned again
22:05Z     get Feedback/Feedback/Source/FeedbackHandover → Not found
```

The same line fired on eleven boots from 2026-09-13T22:02Z on — each one matched one-to-one by a
`Plugins/Feedback` record version (v18…v28) stamped seconds later, and no record version without
it — and each one re-opened or re-counted MeshWeaver#2387 through the log watcher's category fold. On 2026-09-14 the eight seconds between "named ABSENT" and the node's `lastModified` were read
as a repair that WORKED, and the detection was demoted to a Warning on that basis (#4257): a
read-back right after the write proves the write landed, never that it held. The control instance,
whose `Feedback` listing carries no `_GitSync`, has one writer and holds the node. See
[Log watch triage](../LogWatchTriage) → "A REOPEN is not a recurrence".

**What closes it, and what delivers it.** `115e0a9d9c` (PR #4292) is the commit: it makes this lane
list and install at the sealed commit, so both writers of `Feedback` land on one tree; `8b1e966985`
(PR #4364) adds the ownership hold, and `4d5a084a8b` (PR #4257) moves the severity off the detection.
Measured 2026-09-17, none of the three is an ancestor of memex.meshweaver.cloud's running core
`c84c6c05`, and all three are ancestors of memex.systemorph.com's `afde4eab` — which is why only the
former still emits it. The delivery is a **Roll of memex-cloud onto a sealed image containing
`e76fa9f8f2`** (the newest of the three merges); nothing else closes it, and no further code change
is required. Its preconditions already hold on that instance: `pluginCatalog.sources[0]` carries
`repoPath: https://github.com/Systemorph/MeshWeaver.Plugins` (not a local checkout),
`PreWarm__PrebuiltBundleRoot` is `/data/prebuilt-bundles`, and the seal is demonstrably readable
there — its own `Feedback/_GitSync` prints *"identity sd608997…: 'plugins' is sealed at 627fb3cd"*.

**The fix is the rule, applied to the lane that had missed it.** The boot install now asks
`SealedSyncGate.DecideFirstImport` per configured git source — the very decision the first import
takes — and lists *and* installs at its answer:

| what the seal says for the source's repository | what the boot install does |
|---|---|
| no publication of it is sealed for this identity | the configured ref — the residue's case, unchanged (a course repo on a registry, a local checkout, a registered `IPackageSource` with no repository) |
| one sealed commit, attributed by the `repository.txt` marker | list and install at that commit; the record's `installedFromRef` carries the sha |
| torn, at an unknown commit, or two seals disagreeing | **hold the source this boot** — nothing installed at the branch instead, the pass marks its listing incomplete (#4097) and says which seal it waits for |

So on the instance above every unattended writer of `Hosting` lands on `627fb3cd` until the image
rolls, and when it rolls the seal moves them together. A registry still *serves* its sources at the
configured ref over `/api/plugins` — that is a listing for a person to install from, and the
consumer's own boot lane applies this rule on its side when it can attribute a seal.

🚨 **A hold here releases at the NEXT PROCESS START — everywhere, not only on a webhook-less
instance.** That is a narrower condition than the first import's: the boot lane runs once per boot,
after the bake settles, and subscribes to neither `PublicationSealArrivalService` nor
`SealedPublicationSyncReconciler`, deliberately (no timer, no retry, no watchdog — the class's own
rule). A seal that completes mid-process is therefore installed at the next boot, and the log line
says so in those words. The sync side of the same partition *does* follow that mid-process arrival
(#4209), and the two do not fight in between: the boot lane writes nothing again until it runs
again, and then both are on the new seal. The hold is deliberately not a failure — a retry cannot
change a seal, the seal landing can — so it is neither ledgered as failed nor re-attempted in a loop.

🚨 **The seal is read on the FileSystem `IIoPool`, never inline** — the published root is a mounted
share, and a read of it off the pool is invisible to the registry's teardown drain and blocks
whatever thread the install chain is on while the share is slow; `PublicationSealArrivalService`
reads it the same way. A read that faults holds the source — an unobserved seal is not a clean one —
and a mesh that has a published root but no pool registry keeps the configured ref and says so,
rather than running untracked I/O.

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

## The human exception, withdrawn (MeshWeaver#3845 hole 3)

Until 2026-09-17 two GUI paths — and the MCP verb that shares their code — read whatever a person
asked for, whatever the seal said:

| path | what it read |
|---|---|
| `GitHubActionArea` → **Update from GitHub** (`op=update`), and MCP `git_hub_sync` `update` | `UpdateToLatestFromGitHub` → the configured branch, resolved at fetch time |
| `GitHubSyncSettingsTab` → **Re-import at this commit** | `ReimportFromGitHub` → the typed commitish; the field's placeholder reads *"commit SHA or branch"*, so a tip was a legal entry |

The rule then was *unattended reads a proven commit, a present human may read a tip*. It was written
for #1430 — a ref resolved twice — and it is right about that. What it did not account for is the
other half of #3583: **a person present for the result does not change what the result is.** A
module repository's sources landing on a tree no bundle for this identity was baked from are declined
on their source fingerprint and compiled from source (or fail to compile) exactly as they are when a
webhook put them there; the NodeType leaves `AdoptedVerified` either way. The contract keyed the
protection on *who asked* when the harm is keyed on *what lands*.

**Now both paths ask the seal, in `DecideFirstImport`'s shape** (`SealedSyncGate.DecideRequestedImport`).
The decision is taken inside the activity, so what was decided is on the activity's own log, in the
viewer's language:

| what the seal says for the Space's repository | what the import does |
|---|---|
| the index could not be READ | **imports nothing** — "cannot tell" is never "clear to proceed" (#3461) |
| no publication of it is sealed for this identity | **exactly what was asked** — the branch, or the typed commitish. Hole 1's adjudication: a repository no lane publishes could never be released from a hold, so it is not held |
| sealed, every attributable publication usable and agreeing on commit `C` | **imports `C`**. When `C` is not what was asked, a Warning line names both, so the activity ends *Warning* — never a quiet *Succeeded* for a tree nobody requested |
| attributable but torn, at an unknown commit, or two seals disagreeing | **imports nothing**, and says which publication and why |

Every hold and every redirect carries a second line naming the direction, from the same
`SealedPublicationIndex.NewerLineThan` reading the webhook's hold note uses (#4063): when the registry
has sealed a newer line this instance does not run, **roll this instance**; otherwise the Space
advances when a newer commit is sealed for this identity by the publishing lane, or when the instance
rolls onto a platform whose publication is. The line never picks one of those two when the release
markers cannot place this instance on a line.

### What it costs, stated rather than discovered

- **Recovery from a hold is not a tip import any more.** It is what the hold names: roll the
  instance, or fix the publishing lane. On 2026-09-14 this was the stated reason *not* to close the
  hole (*"the escape hatch is gated by the thing you are escaping"*). It is now the contract, and
  deliberately: an escape into a tree the instance cannot run is not a recovery.
- **Re-import at this commit is still the repair for drift** — at the sealed commit. Typing the
  sealed sha, or a prefix of it of seven or more characters, runs exactly as asked.
- **The git-first loop on a module repository is slower.** Step 3 of that loop in
  [GitHub Sync](../GitHubSync) — *Update to latest pulls the merged state back* — now pulls the merged
  state only once it is sealed for this identity. Until then the Space stays on the sealed commit and
  the activity says why. A repository this instance runs no publication of is unaffected.
- **Every caller of the two extension methods is gated, not only the two buttons** — the MCP
  `update` verb, and anything in the mesh that calls `UpdateToLatestFromGitHub`. That is a behaviour
  change behind an unchanged signature, on purpose. There is **no flag** that reads a tip for an
  attributable repository: `force` still means *discard local edits* and never selects the tree. A
  bypass would be the fail-open this page exists to remove, with a parameter's name on it.

### What is deliberately unchanged

- `ModuleDiscoveryService.FirstImport` still decides for itself (`DecideFirstImport`) and calls
  `UpdateToLatestFromGitHub` only for an unattributable repository, where this gate answers "as
  asked" by construction.
- A repository is attributed exactly as the webhook attributes it: by the seal's `repository.txt`
  marker, or — for a seal that predates the marker — by commit (the typed sha, or the commit the Space
  already sits on).
- 🚨 **One residual, named.** Attribution compares the Space's STORED repository url. A url still
  carrying a pre-rename name matches no marker until the webhook's rename repair
  (`ConfigsTargeting` → `RepointToCanonical`) rewrites it on that repository's next delivery; until
  then a person's update of that Space reads as asked.

## The webhook lane lands on the seal — it no longer only holds

The green-build webhook was the last unattended lane that asked the seal and, on "not sealed for this
instance", merely **held**. The first import (#4212), the seal's arrival
(`SealedPublicationSyncReconciler`, #4209) and the boot install (#4259) all LAND on the sealed commit.
The webhook now does too (`SealedSyncGate.DecideBuild`):

| the seal says for the build's repository | the webhook does |
|---|---|
| nothing attributable | import at `head_sha` — unchanged |
| a sealed publication at `head_sha` | import at `head_sha` — unchanged |
| sealed at `C ≠ head_sha`, every attributable publication usable and agreeing | **import at `C`** — unless the source already sits on `C` (then the hold is recorded on its config, naming `C`, as before), or `C` already carries a final verdict for this source (skipped — the same #4499 predicate the reconciler asks) |
| torn, unknown commit, or disagreeing | hold — unchanged |

Why this is safe: `C` is exactly the commit the reconciler imports the next time it reads the seal;
the webhook simply stops waiting for that read. It can move a Space **backwards** — a Space imported
at a tip before this change, under a seal at an older commit, returns to `C` — and that is the rule,
not a side effect: afterwards the sources match the bytes. The census still counts the source as held
for that build (`publication-seal` on `/health`): the build's commit did not arrive, and that is the
fact the census states.

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
- `test/MeshWeaver.Hosting.Test/APersonsImportLandsOnTheSealTest.cs` pins the withdrawn human
  exception on the same instrument — the ref that reaches `Fetch`: **Update to latest** and a
  **Re-import** of a typed branch fetch the sealed commit, a torn seal fetches nothing and names the
  publication, and a repository this instance runs no publication of still fetches the branch (the
  control that keeps the other three from passing on a gate that blocks everything).
  `HeldSourceSaysItIsHeldTest` pins the webhook landing on the seal, and the hold note a source
  already on the seal records. The decisions themselves, both directions, are
  `test/MeshWeaver.Documentation.Test/SealedSyncGateLandsOnTheSealTest.cs`.
- On a live portal, the import's own activity names the commit; the sync source records it as
  `lastSyncCommitSha`, which is now the same value the webhook filtered on, so
  `GitHubWebhookProcessor.SkipReason`'s "already at this commit" compares like with like.
- After any wave that moves plugin sources, the readiness sweep is one call:
  `search 'nodeType:NodeType content.compilationStatus:Error partitions:all' limit:200`, read
  against its envelope's `coverage.partitions` (the bare form is refused, #4274). A
  `searched: false` envelope is a FAILED sweep, not a clean one.
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
