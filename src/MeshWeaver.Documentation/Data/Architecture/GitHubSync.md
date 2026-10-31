---
NodeType: Markdown
Name: "Syncing a Space with GitHub"
Abstract: "How to connect a Space to a GitHub repository and move content both ways: sync FROM GitHub (import a repo to create a Space, update to latest, re-import at a commit) and sync TO GitHub (commit/export the Space's nodes as a single commit). Plus the repo operations: create a branch, commit, checkout / update to latest, and open a pull request (AI-drafted, you edit, then submit) with live PR status sync and a link. Plus browsing and acting on the repo's issues and pull requests: sync issues into the Space as nodes, create + comment on issues, list PRs live and merge them, and keep issues fresh via HMAC-verified GitHub webhooks. Covers the per-user OAuth connection, the Space settings, and how operators enable it."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#24292f'/><path d='M12 4a8 8 0 0 0-2.53 15.59c.4.07.55-.17.55-.38l-.01-1.34c-2.23.48-2.7-1.07-2.7-1.07-.36-.92-.89-1.17-.89-1.17-.73-.5.06-.49.06-.49.8.06 1.23.83 1.23.83.71 1.22 1.87.87 2.33.66.07-.52.28-.87.5-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.83-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.22 2.2.82a7.6 7.6 0 0 1 4 0c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.52.56.83 1.28.83 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48l-.01 2.2c0 .21.15.46.55.38A8 8 0 0 0 12 4Z' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "GitHub"
  - "Data Sync"
  - "Spaces"
  - "Setup"
---

# Syncing a Space with GitHub

A Space can be connected to a **GitHub repository** and its content moved in both
directions:

- **Sync FROM GitHub (import)** — read a repo to **create a new Space**, **update an
  existing Space to the latest** state of its branch, or **re-import** it at any
  branch or commit (bringing it to exactly that state).
- **Sync TO GitHub (commit/export)** — write the Space's nodes into the repo as a
  single commit. The repo mirrors the Space subtree. *A sync is a commit.*

On top of the two sync directions, the tab exposes the everyday Git **operations** you
expect on a repo: **create a branch**, **commit** (a "Sync now"), **checkout / update
to latest**, and **open a pull request** — drafted by AI, edited by you, then submitted,
with its **status read live** from GitHub and a **link** to the PR. Every operation runs
as a tracked activity, so you see **progress** and can **cancel** it.

Everything is configured in the Space's **Settings → GitHub Sync** tab. Your GitHub
connection is **personal**: you authorize once with your own GitHub account, every
commit and pull request is authored as you, and your token never leaves your account.

For the underlying, fingerprint-gated *import-source* model (platform content synced
from a repo at a release tag), see [DataSyncSetup.md](/Doc/Architecture/DataSyncSetup).

---

## 1. Connect your GitHub account (once)

GitHub Sync authenticates with a **long-standing OAuth credential** via GitHub's
**authorization-code flow** — no password, no pasted personal access token.

1. Open any Space → **Settings → GitHub Sync**.
2. Under **Your GitHub account**, click **Connect GitHub →**.
3. Your browser is redirected to GitHub; approve the authorization (authorize it for
   the **org** whose repos you'll sync). GitHub redirects back to the portal
   (`/connect/github/callback`), which stores the token and returns you to the Space.
4. The tab shows **✓ Connected as _your-login_**. You can **Disconnect** anytime.

Your token is stored **encrypted at rest** (AES-256-GCM) on your own partition
(`{you}/_Provider/GitHub`) and is reused for every sync — you only connect once.
It is never written into exported content.

> If you see *"GitHub OAuth is not configured"*, the server has no OAuth App set up
> yet — see [§5 Operator setup](#5-operator-setup-enabling-the-feature).

---

## 2. Point the Space at a repository

Under **Repository**:

| Field | Meaning |
|---|---|
| **Repository URL** | `https://github.com/owner/repo` — or an Azure Repos URL, which syncs the same way (see **Azure Repos** below). |
| **Branch** | The branch to commit to (default `main`). |
| **Sync direction** | `Bidirectional` (default), `ExportOnly`, or `ImportOnly` — see below. |
| **Create the branch if it doesn't exist** | When on, a missing branch is created as a fresh snapshot commit. |
| **Create the repository (private) if it doesn't exist** | When on, a missing repo is created **private** under the owner/org. |
| **Subdirectory** (optional) | Mirror the Space into this folder of the repo. Files outside it are left untouched. Empty = repository root. |

Click **Save repository settings**.

### Sync direction — unidirectional or bidirectional

Each source syncs in a configurable direction, enforced by `GitHubSyncService` on every
operation (the GUI additionally hides what would be rejected):

| Direction | Commit ("Sync now") | Checkout / re-import |
|---|---|---|
| **Bidirectional** (default) | ✓ | ✓ |
| **ExportOnly** — mesh → repo | ✓ | ✗ rejected — the repo can never overwrite the mesh |
| **ImportOnly** — repo → mesh | ✗ rejected — the mesh can never overwrite the repo | ✓ |

Use `ImportOnly` for a Space that mirrors an upstream repository you don't own, and
`ExportOnly` for a backup/publishing target that must never feed edits back.

### Azure Repos — the same sync, over the Azure DevOps REST API

A Space can also be **synced with an Azure Repos repository**, for an organisation whose content must
live on Azure DevOps (policy [`azure-repos-two-way`](../PolicyNotProse), MeshWeaver#6500; it
supersedes the push-only first step, `azure-repos-push-only`, MeshWeaver#5248). Put the Azure Repos URL
in **Repository URL** — either shape is recognised:

- `https://dev.azure.com/{org}/{project}/_git/{repo}` (also with the `https://{org}@dev.azure.com/…`
  prefix the clone dialog adds), or
- the legacy `https://{org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}`.

**The provider is read off the URL, never stored** (`GitRepositoryProvider.Classify`). The settings tab
says under the Repository section which service a source syncs with. The config keeps its name
(`GitHubSyncConfig`): renaming a type that is persisted on every synced Space, and that in-mesh C# may
name, would need a migration of every stored `$type` and could break code CI never compiles. A stored
`Provider` field could also disagree with the URL beside it.

**One seam, one sync.** Every GitSync caller talks to ONE `IGitHubRepoClient`, and the production
registration is `GitProviderRepoClient`: it sends each call on a URL a registered **git hosting
provider** claims (Azure Repos is one — see *Hosting providers* below) to that provider's client
(`AzureReposRepoClient`), and every other URL to the GitHub client. Everything above the client — the
import, the export, the authored-content comparison, the type protections and the two-way rules — is
therefore the same code for every provider. `GitProviderRepoClient` forwards every interface member
explicitly, the default-implemented ones included: inheriting a default would bind the *fallback* (a
full fetch for a head lookup, "diff unknown" for every diff) instead of the provider's own.

`AzureReposRepoClient` speaks the Azure DevOps Git REST API (`api-version=7.1`), each call an IO leaf on
the HTTP `IIoPool`:

| Operation | Azure DevOps call |
|---|---|
| Resolve a branch, tag or commit | `GET …/refs?filter=heads/{branch}` (exact name — the filter is a prefix), `refs?filter=tags/…`, `commits/{id}` |
| Snapshot (import, re-import, update to latest) | `GET …/items?scopePath=/{subdirectory}&recursionLevel=Full&versionDescriptor.version={sha}`, then `blobs/{id}?$format=octetstream` for the files the filter selects |
| Export ("Sync now") | `GET …/repositories/{repo}` (exists? default branch), the blobs under the subdirectory at the head, then ONE `POST …/pushes`: an `add` per new path, an `edit` per path whose git blob id changed, a `delete` per path no longer exported, all `base64encoded` (binary-safe). The ref's `oldObjectId` is the head that was read, so a concurrent push is refused by Azure DevOps instead of overwritten |
| A missing branch | `POST …/refs` at the default branch's head (never an orphan), then the push; an empty repository gets its first commit from the push itself |
| Diff since the last sync | `GET …/diffs/commits` — used only when `commonCommit` is the base and `allChangesIncluded` is true; otherwise the import reads the full snapshot |
| Ancestry | `diffs/commits`'s `commonCommit` equals the ancestor |

**What is the same as GitHub** — pinned side by side by `AzureReposGitHubParityTest` (Hosting.Test),
every scenario a theory over both providers: export, import into a new Space, the mirror within a
subdirectory, update to latest and the branch state, the two-way rules (a server addition survives every
later repository commit; a server edit newer than the horizon is preserved, holds both the baseline
commit and the conflict horizon, and is carried back by the next export) and the direction refusals.

**What differs, and why it is invisible to the sync:**

| | Azure Repos |
|---|---|
| An export that changes nothing | creates no commit (the pushes API refuses an empty commit); the result names the branch head, which holds exactly the exported content — what the sync records either way |
| Create the repository if missing | not done — a missing repository (or one the identity cannot read) is refused by name, `gitsync.azure.repositoryNotFound` |
| Inbound sync | **on demand** — *Update to latest*, *Re-import at commit*, *Check branch*. The GitHub push webhook, the branch reconcile and the sealed-publication reconcile are keyed to a GitHub `owner/repo`, which an Azure Repos URL never resolves to (`GitHubRepoIdentityResolver.Parse` answers null), so none of them reaches an Azure Repos source. Read positionally, `dev.azure.com/{org}/{project}/…` would have yielded `{org}/{project}` — some GitHub repository's name, whose webhook would then have imported into the Azure source |
| Pull requests, issues | not offered — refused by name (the provider's `Refuse`, `gitsync.azure.unsupported.*`), and the settings tab shows a notice under the draft button |

**The credential is the instance's workload identity, declared per client tenant.** Nothing secret is
stored: no PAT and no client secret. Every read and every push authenticates with an Entra access token
for the Azure DevOps resource (`499b84ac-1321-427f-aa17-267ca6975798/.default`), sent as
`Authorization: Bearer`. `WorkloadIdentityCredential` mints it from the instance's workload identity,
through a **federated credential on an app registration in the client's tenant**. That registration is
declared in the instance's deployment record:

```jsonc
// Deployment record: config.memex_portal.AzureDevOps__TenantId / AzureDevOps__ClientId
// (delivered verbatim by the portal ConfigMap's pass-through) → configuration section:
"AzureDevOps": { "TenantId": "<client tenant id>", "ClientId": "<app registration client id>" }
```

Both values are identifiers, not secrets. On the client's side:

1. Create (or reuse) an app registration in the client tenant.
2. Add a federated credential for the instance's AKS workload identity: the cluster's OIDC issuer and
   the portal's service account as subject.
3. Add that registration to the Azure DevOps organisation as a user, and grant it these **repository
   permissions** on the target repository. Azure DevOps controls each one separately:
   - **Read** — every import, and the export's read of the branch head.
   - **Contribute** — to push the sync commit.
   - **Create branch** — only when the target branch may not exist yet and *Create the branch if it
     doesn't exist* is on (the default). To avoid granting it, pre-create the branch and turn that
     setting off; a missing branch is then refused by name.

   The repository itself must exist: GitSync does not create Azure Repos repositories, whatever
   *Create the repository* says.

**Prerequisite on the instance: the portal pod must carry a workload-identity token.** The AKS
workload-identity webhook projects the service-account token (`AZURE_FEDERATED_TOKEN_FILE`) only into a
pod labelled `azure.workload.identity/use: "true"`. Today the chart sets that label when the deployment
record declares `selfUpdate.azureClientId`. Declaring only the two `AzureDevOps` keys does not arm it,
and every Azure Repos operation is then refused by name (`gitsync.azure.noWorkloadIdentity`), never with
the SDK's generic error. The self-update client id the webhook injects does not interfere: the Azure
DevOps exchange uses the declared tenant and client id, presented with the same projected token.

When either key is absent every Azure Repos operation is refused, naming both keys
(`AzureReposTokenService`). Every refusal on this path is a catalog key rendered in the viewer's
language (`GitSyncRefusal`). App-identity commits are authored as `MeshWeaver <noreply@meshweaver.cloud>`.

**Not yet:** Azure DevOps service-hook ingestion (an inbound sync without a click), Azure DevOps pull
requests, and contract tests against a real Azure DevOps organisation — the suite fakes Azure DevOps at
the HTTP boundary (`FakeAzureDevOpsServer`), so the REST calls, payloads and Bearer credential are
exercised but the service itself is not. The rules, the seam and the client's pure planning (mirror
changes, git blob ids, the push body, the diff rules) are pinned by `AzureReposSyncPolicyTest`
(MeshWeaver.Documentation.Test); the provider-neutral seam by `GitHostingProvidersTest`. Azure Pipelines is a separate concern:
[Hybrid delivery](../HybridGitHubAzureDevOps).

### Hosting providers — how a service other than GitHub plugs in

GitSync knows GitHub (REST plus the git protocol) and plain git remotes itself. Every other hosting
service is an `IGitHostingProvider` a module registers with `services.AddGitHostingProvider(sp => …)`
beside `AddGitHubSyncServices()`. A provider declares:

| Member | What GitSync does with it |
|---|---|
| `Kind` | its name in the open provider vocabulary (`GitRepositoryProvider.GitHub` and `.Git` are the platform's and cannot be taken) |
| `Claims(url)` | whether a repository URL is hosted there — resolved on every call, never stored on the source |
| `RepoClient` | the `IGitHubRepoClient` every operation on a claimed URL runs on (`GitProviderRepoClient` routes to it) |
| `GetAccessToken()` | the instance's own identity for that service — every read and push on a claimed URL uses it, never a user's GitHub credential |
| `Refuse(url, operation)` | what it does not offer (`GitHostingOperation.PullRequest`, `.Issues`), refused by name before any credential is asked for; an operation it does offer runs with `GetAccessToken()` |
| `CommitAuthorName` / `CommitAuthorEmail` | the author of an export (default `MeshWeaver` / `noreply@meshweaver.cloud`) |
| `DisplayNameKey` | the settings tab's one-line description (default `ui.gitSync.provider.{Kind}`) |

`GitHostingProviders` is the one place a URL is resolved, and it never picks silently: two providers
of one kind, or a provider taking a platform kind, refuse the registry when it is built; a URL two
providers both claim is refused by name (`gitsync.provider.ambiguous`) wherever it is resolved — on
subscribe, so a composed call never throws. A URL nobody claims is the platform's own GitHub / git path.

The push webhook, the branch reconcile and the sealed-publication reconcile are keyed to a GitHub
`owner/repo`, and `GitHubRepoIdentityResolver.Parse` gives one only to `{host}/{owner}/{repo}`: an
http(s) URL with a deeper path (Azure Repos' `{org}/{project}/_git/{repo}`, a GitLab subgroup) answers
null. The rule is structural, so it needs no knowledge of which providers are registered.

**Where Azure Repos lives.** The platform still registers its own Azure Repos provider as a BUILT-IN
(`AzureReposGitHostingProvider`); a contributed provider of the same kind replaces it. That is how the
provider leaves the platform without a gap: the module that carries it (with `Azure.Identity`) lands in
MeshWeaver.Plugins first and takes the kind over, and only then does the platform delete its copy —
see [Carving Projects Out Of Core](../CarvingProjectsOutOfCore).

### Multiple sync sources

A Space can sync with **more than one repository**. The Repository section above edits
the **primary** source (`{space}/_GitSync`); every additional source is its own config
node at `{space}/_GitSync/{sourceId}` with its own repository, branch, subdirectory and
direction. Manage them in the **Additional sync sources** section of the same settings
tab (or, platform admins, on **Admin app (`/Admin`) → Partitions**): add a
source by name, edit its settings through the same data-bound editor, sync it with its
own direction-aware buttons, and remove it when no longer needed. Programmatically:
`GitHubSyncService.AddSyncSource / WatchConfigNodes / RemoveSyncSource`, and every sync
operation takes an optional `sourceId` (null = the primary).

---

## 3. Sync TO GitHub — commit ("Sync now")

Click **Sync now**. The Space's content nodes are serialized and pushed as **one
commit** — *a sync is a commit*:

- Markdown pages → `*.md` (with YAML frontmatter); typed nodes → `*.json`; code → `*.cs`.
- A node that has children is written as `Name/index.md` so its children live in the
  `Name/` folder; the Space itself is the top-level `index.json`.
- **Satellites and secrets are never exported** — access assignments, threads,
  activities, notifications, pull requests, the GitHub credential, and the sync config
  are all skipped.
- **Mirror semantics:** within the configured subdirectory the repo is made to match
  the Space exactly — nodes you deleted are removed from the repo. Anything outside the
  subdirectory is untouched.
- **Commits on the branch HEAD.** The commit is parented on the current branch HEAD
  (read from GitHub at commit time), so it lands *on top of* whatever is already there —
  GitHub does the commit; we never overwrite history. Files outside the subdirectory are
  carried over by reference, so the rest of the repo is left intact.

When it finishes, the resulting **commit SHA is stored on the Space** and shown as
*"Last synced: … — commit …"*. (The stored SHA is a record of *your last sync action*,
not a replica of the branch state — the branch's live HEAD always lives on GitHub.)

---

## 4. Sync FROM GitHub — import, update to latest, re-import

**Create a new Space from a repository.** Importing a repo into a brand-new Space
provisions the partition, makes you its admin, and imports every node. (Programmatic
entry point: `GitHubSyncService.ImportFromGitHub(repoUrl, commitish, newSpaceId, name,
subdirectory, userId)`.)

**Update an existing Space to the latest.** "Update to latest" re-fetches the
configured **branch HEAD** and mirrors it into the Space (add / update / prune) — this
is the *checkout* operation: it brings the working Space up to whatever is now on the
branch.

**Re-import at a chosen commit.** Under **Sync**, the **Commit or branch to import**
field is pre-filled with the last synced commit. Change it to any commit SHA or branch
and click **Re-import at this commit** — the Space is mirrored to that exact state
(added / updated / removed to match), and the new commit is recorded. This is how you
roll a Space forward or back to a specific repository state.

> 🚨 **For a repository whose compiled modules this portal runs, "latest" means the latest this
> portal can RUN** (since 2026-09-17, MeshWeaver#3845). When a publication of the repository is
> sealed for the portal's framework identity, both buttons import **the sealed commit** — the commit
> the portal's bundles were baked from — whatever branch or commit was asked for, and the activity
> says so in a Warning line naming both. When that publication is torn, at an unknown commit or
> disagreeing, they import **nothing** and say which publication holds the Space and what releases
> it: **roll the portal** when a newer platform line is sealed, otherwise the publishing lane sealing
> a newer commit. A repository this portal runs no publication of (a course, a document tree, a
> deployment record) is unaffected and reads exactly what was asked. The rule and its reasons:
> [The Sync-Ref Contract](../SyncRefContract).

Import reuses the platform's content-addressed import pipeline (fingerprint gate +
activity lock + canonical upsert + prune) — see
[StaticRepoImport.md](/Doc/Architecture/StaticRepoImport).

### Two-way — never overwrite changes made on the server

By default import is **git-first**: an update overwrites (and prunes) the live node from the
repo. That silently loses edits made on the server *between* syncs. Turn on **Two-way** (a
checkbox on the sync source) to change the conflict rule to **newest-writer-wins per node**:

- A node whose live `LastModified` is newer than the last recorded sync (`LastSyncedAt`) was
  changed on the server since you last synced. An **Update to latest** **keeps** that node —
  it is neither overwritten nor pruned — so your local change is carried back to GitHub on the
  next **Commit** ("newer on the server wins → GitHub"). A node the server hasn't touched since
  the last sync is still updated from the repo as usual.
- Two-way only takes effect once a first sync has recorded `LastSyncedAt`; the initial import is
  git-first (there is nothing local to protect yet).

**Force update** is the escape hatch: it ignores two-way and overwrites/prunes from the repo
regardless — use it to deliberately discard local changes back to the repository state. Via MCP:
`git_hub_sync(space, op:"update", force:true)`.

> **Take-over edits survive.** A node you've edited and marked to exclude from sync is
> not overwritten or pruned by a re-import — that's how you "claim" content locally.
> Two-way generalizes this to *every* server-side edit (no explicit claim needed) as long as it
> post-dates the last sync.

### The four facts on a sync source — and why they disagree on purpose

A sync source records **four separate facts**, and reading any one of them as the answer to
another's question is how an investigation goes wrong. They are deliberately independent:

| Field | The question it answers | When it moves |
|---|---|---|
| `lastSyncAttemptAt` + `lastSyncOutcome` | *When did a sync last RUN here, and what did it conclude?* | **Every** conclusion — an import, a no-op, one that preserved server-side edits, one that landed nothing. |
| `lastSyncCommitSha` | *Which repo commit has this Space already got?* | Whenever the mesh genuinely reached that commit — **including** a no-op update, so a repo commit touching no node files does not leave the Space forever "behind". |
| `lastSyncedAt` | *When were mesh and repo last RECONCILED?* — the two-way **conflict horizon** | Only on an import that really reconciled: **not** on a fingerprint-matched no-op, **not** when server-newer nodes were preserved, **not** when something failed to land. |
| `lastAttemptedCommitSha` + `lastAttemptWasFinal` (+ `lastAttemptedConfigFingerprint`) | *Have we already LOOKED at exactly these bytes, as this source is configured now, and could looking again change the answer?* | On every import conclusion, a refusal included; **cleared** by an export and by a hold. This is the pair that makes a green build or a publication announcement free for a source that cannot converge, and the fingerprint is what lets an edit of the source re-attempt at the same commit — see [What a Green Build Costs a Synced Space](/Doc/Architecture/GitSyncTriggerCost). |

GitSync reads authored JSON with strict member matching. If a repository record contains a
field the running image does not know, it refuses the **whole snapshot** before writing nodes.
The activity and the source's `lastSyncNote` name the offending file and field;
`lastSyncCommitSha` and `lastSyncedAt` stay put. The attempted SHA is recorded with
`lastAttemptWasFinal = false`, so the same commit remains eligible after the image gains the
new record type. A malformed node file follows the same all-or-nothing rule; files with no
node parser, such as ordinary `.py` files, remain outside the import.
The mesh serializer also emits `$type` on sealed nested records, such as a deployment's
`keyVaultSecrets` and `gates` entries. These markers describe the record type, not an
authored field: the strict importer recognizes them while still refusing any other
unknown member at that same nesting level. Without that distinction, the first control
roll after strict import refused five valid deployment files
([Memex #601](https://github.com/Systemorph/Memex/issues/601)).

#### A compiled NodeType's abstract member, and a subtype nothing here knows

Strictness is about **lossy** reads, never about content this process merely cannot type
([#6639](https://github.com/Systemorph/MeshWeaver/issues/6639)). Two rules keep the line in the right
place:

- **A compiled build's subtypes come from that build.** A runtime-compiled NodeType's content is
  reached on the importing hub through the mesh content-type map, because a compiled type is never
  adopted into a long-lived hub's type registry. Its members typed as an abstract base
  (`SectionCover Cover`) were then unreadable there: the polymorphic resolver looked for subtypes
  only in that registry, found none, and the serializer refused the abstract member — a forced
  update refused a whole Space over fourteen valid section files. `PolymorphicTypeInfoResolver` now
  reads the subtypes of a base from a collectible assembly out of that same assembly, under the
  discriminator the owning hub writes (the short name), without registering anything. A short name
  two subtypes of the same build share is added for NEITHER — binding whichever the assembly
  enumerated first would put one record's content into the other and silently drop members — so
  that content stays raw JSON.
- **A subtype nothing can resolve is kept verbatim.** When a `$type` under an abstract member names
  a type no build in this process defines, the content stays as its raw JSON and is imported
  byte for byte. Only a `JsonException` — an authored member this image would DROP — still refuses
  the snapshot; the "cannot instantiate an abstract type" failure is not lossy and no longer does
  (`JsonElementNormalizer.KeepsRawJson`, the one rule for the object converter and the content-type
  recovery).

`CompiledAbstractMemberImportsTest` loads its types from a collectible copy of the test assembly —
the state a compiled NodeType leaves a mesh hub in — and measures both rules.

#### A retired `MeshNode` field is a build error, not a tolerated field

Strict reading cannot tell a member `MeshNode` **no longer has** from one an older image does not
know **yet** — both are unmapped. The platform stays strict anyway: there is no tolerance list and no
compatibility shim in the reader. The content is migrated instead, and the rule is enforced where
the content is written
([#6638](https://github.com/Systemorph/MeshWeaver/issues/6638)):

- **The register.** `.github/meshnode-fields.json` lists `MeshNode`'s JSON `members` and the
  `retired` ones, each with what to do instead (`isPersistent` — remove it; `displayOrder` — use
  `order`; a top-level `requires` — dependencies live in the package root's `content.requires`; …).
  `MeshNodeFieldsRegisterTest` keeps `members` equal to the type by reflection, so removing a member
  from `MeshNode` fails that test until its name moves to `retired`.
- **The gate.** `.github/scripts/check-retired-node-fields.py` fails on any node file whose top level
  carries a retired member, naming the file, the field and the replacement. It runs on core's own
  trees (`samples/Graph/Data`, the documentation tree) and in every node repo's validate lane, so the
  pull request that adds or keeps one is red — the snapshot refusal never reaches a portal.
- **The runtime refusal.** If such a file reaches a repository anyway, the snapshot is still
  refused, and the note says `field 'isPersistent' is not a member of MeshNode (a retired or
  misspelled field) — remove it from the file`, with the file path.

Retiring a `MeshNode` member is therefore a change in three places in one pull request: the type,
the register, and every node file in core that carries it — and a counterpart change in every node
repo that carries it, merged **before** the core change, so no satellite turns red.

The horizon is the one with teeth. Everything newer than it counts as a pending server-side change
and is protected from overwrite and from the prune, so advancing it past uncommitted work disarms
exactly the protection two-way exists for — a later push would then delete that work. That is why
the suppressions are there, and why **the horizon must never be made to track "when did we last
sync"**.

> 🚨 **A frozen `lastSyncedAt` beside a fresh `lastSyncCommitSha` is not a bug.** Measured on
> 2026-09-07, `Edu/_GitSync` read a `lastSyncedAt` of 2026-07-11 (control instance) and 2026-08-07
> (public instance) beside a `lastSyncCommitSha` from that same morning. Both were correct: every sync
> in between had been a no-op at unchanged content, which advances the commit and holds the
> horizon. What was missing was the third fact — nothing recorded that a sync had run at all, so
> the only way to date one was to compare node timestamps against image tags in a container
> registry. `lastSyncAttemptAt` is that fact, and the settings tab now shows the three separately
> instead of printing the horizon under the words *"Last synced"*.

> 🚨 **And a frozen `lastSyncAttemptAt` is not necessarily a dead webhook.** A source whose last
> attempt reached a FINAL verdict at a commit is deliberately skipped for every later delivery of
> that same commit, so its recency stamp stops moving until the repository produces a new one.
> The settings tab says so in as many words — *"this commit has a final verdict — the next new
> commit re-attempts"* — precisely so the stopped clock cannot be read as a stopped delivery.

Note that a node's own `lastModified` is **not** a substitute: `stream.Update` does not re-stamp
it, so a node can be rewritten without its modification time moving.

### Git is the source of truth — author in the repo, never only-live

A sync **reconciles**: *Update to latest* and *Re-import* mirror the branch into the Space
with **add / update / prune** — so **a node that exists live but is NOT in the repo is
PRUNED.** That is the model's whole point (the repo is authoritative and reproducible),
but it has one sharp edge worth stating plainly:

> ⚠️ **Content created or edited *only live* — never committed — is deleted by the next
> reconcile.** A restart, a scheduled restore, or someone clicking *Update to latest*
> re-imports the repo baseline and prunes everything not in it. This is the single most
> common way live work is lost. **Author in the repo.**

The safe loop for anything you want to keep — the **git-first** discipline:

1. **Edit in the repo** — or, if you edited live, **Sync now** (`op: commit`) *immediately*
   to capture it in the repo; never let live-only state accumulate.
2. **Commit / open a PR**, review, **merge**.
3. **Update to latest** (`op: update`) — pull the merged state back into the Space. On a
   repository whose modules this portal runs, the merged state arrives once it is **sealed** for the
   portal's framework identity; until then the Space stays on the sealed commit and the activity says
   why (see §4).
4. **Recycle** any node whose **type or configuration changed**. Importing new content
   into a node that is already *running* does not swap its live views: a node that flipped
   `Markdown → Deck`, or whose `NodeType` source recompiled, keeps its old hub until you
   recycle it (`{node}/Recycle`, or post a `DisposeRequest`). Freshly *created* nodes get
   the right views immediately; only a **type/config change on an existing node** needs the
   recycle. A node whose content merely changed (same type) re-renders reactively — no
   recycle needed.

### Export never silently drops a node

The export is the exact inverse of the import: **every node serializes** — through a
per-type serializer (`*.md` / `*.cs`) or the **universal JSON fallback** (any content
type → `*.json`, keyed on its `$type`, the inverse of the JSON import). If a node could
ever fail to serialize, the export **fails loudly** rather than skipping it — a node
dropped from the mirror would be pruned by the next import, i.e. **silent data loss**.
Symmetrically, a repo file whose content is missing its `$type` is **tolerated on import**
(the value is re-typed against the target at the read site), so a hand-edited file is not
lost either. The only things left out of an export are the **governance satellites**
(`_Access`, `_Activity`, `_GitSync`, … — see §8) and paths matched by the Space's
**gitignore-style ignore rules** (`SyncIgnore`). Nothing else is excluded, and nothing is
excluded *silently*.

**Adding an Ignore pattern after an import retires what it now excludes (#7611).** The import
prune skips ignored paths, because a node the mesh minted under an ignored path (the default
`Release/` records, #1326) is absent from the repo by design. A node the sync itself imported,
which the Space's own pattern now excludes, is the opposite case. On the next "Update to latest",
such a node is pruned when the Space's import manifest (`{space}/_Activity/import-manifest`)
records that the sync put it there. A node the mesh created under the same pattern was never in
that manifest, so it stays. Before this change, the imported node stayed forever, frozen at its
last version. On a one-way (system-owned) Space no person could delete it either. Held NodeType
sources and the README slot are excluded from the mirror without being retired, so they are
never pruned this way. Regression guard: `AnIgnorePatternRetiresWhatTheSyncImportedTest`.

---

## 5. Operations — branch, commit, checkout, pull request

The tab also surfaces the everyday repo operations:

| Operation | What it does |
|---|---|
| **Create branch** | Creates a new branch from a base ref (a branch name or commit SHA) on the configured repo. |
| **Commit** | The same action as **Sync now** (§3) — a commit IS a sync, parented on the branch HEAD. |
| **Checkout / Update to latest** | Re-imports the Space at the configured branch HEAD (§4) — the working Space is brought to the latest repo state. |
| **Open pull request** | Drafts a PR with AI, lets you edit it, then opens it on GitHub (below). |

### Every operation runs as an activity — with progress and cancel

When you click **Sync now (commit)**, **Update to latest (checkout)**, **Re-import**,
**Check branch on GitHub**, or **Submit pull request**, the operation runs as a tracked
**activity** — not a fire-and-forget call:

- A **progress panel** appears showing the live log ("Committing on the branch HEAD…",
  "Committed a1b2c3d4 (3 written, 0 removed).") and a status badge (**Running** →
  **Succeeded** / **Failed** / **Cancelled**).
- A **Cancel** button is shown while the operation is running. Clicking it requests
  cancellation; the GitHub I/O is cancelled and the activity ends as **Cancelled**.
- The run is **persisted** at `{space}/_Activity/{id}`, so it also shows up in your
  normal activity feed — the same place every other operation in the platform records its
  history. You can revisit the log later.

Under the hood this is the platform's standard **[Activity Control Plane](/Doc/Architecture/ActivityControlPlane)**: the GitHub work runs off the message hub (so the portal stays responsive), progress streams onto the activity node, and Cancel flips `RequestedStatus = Cancelled`. Developers trigger the exact same activities through one unified `IMessageHub` API — `hub.CommitToGitHub(...)`, `hub.UpdateToLatestFromGitHub(...)`, `hub.ReimportFromGitHub(...)`, `hub.CreateBranchOnGitHub(...)`, `hub.OpenPullRequestOnGitHub(...)`, `hub.CheckBranchStateOnGitHub(...)` — each returns the activity path to watch; the GUI and tests call these same methods.

> **Delegate to GitHub — don't replicate.** Every Git operation is performed *on
> GitHub* (create branch, commit on HEAD, open PR, read PR status) — the Space never
> keeps a parallel copy of repository state that could drift. Live state (which branch,
> the branch HEAD, a PR's status) is **asked from GitHub** when you need it. The only
> things the Space persists are its own *local* state: the sync configuration, your last
> *sync action's* commit SHA, and a PR *draft*'s title/body plus the immutable handle
> (number + URL) of a PR once opened. Conversely, content changes coming **from** Git
> only ever enter the Space through the **import pipeline** (import deltas — add / update
> / prune), never by ad-hoc node edits.

### 🚨 A sync operation is refused on anything that is not a synced Space

**Commit**, **Update** and **Check** run *as the System identity* — the click authorizes, the
System executes — because a GitSynced Space is system-owned and no real user holds Create in it.
That elevation rests on a premise: **the target IS a GitSynced Space.** The trigger now checks it
before anything runs: it reads the sync config for the requested source
(`{space}/_GitSync`, or `{space}/_GitSync/{sourceId}`) — after authorization, as System, through the
same `ReadConfig` every operation inside the activity decides on — and unless that config **names a
repository** it **faults, names the path, and creates no activity**:

> Cannot run the GitHub check on 'WhatsNew': it has no GitHub repository configured
> ('WhatsNew/_GitSync' is absent or names no repository), so it is not a GitHub-synced Space and
> nothing was started.

A config *node* is not a configured Space: opening the GitHub Sync settings tab mints `_GitSync`
with an empty repository (`EnsureConfigNode`) before one is chosen, and the sync-source provider
treats that as untracked, so the predicate is the repository URL, not the node's existence. A read
that does not answer within 15 s faults too (localized), never falls through to either branch.

**Why this is a rule and not a nicety ([#4933](https://github.com/Systemorph/MeshWeaver/issues/4933)).**
The trigger is handed *the first path segment of whatever it was called on* — the MCP
`git_hub_sync` tool and the GitHub action page both pass `path.Split('/')[0]` — and until this it
never asked what that segment was. `check` and `update` need only **Read**, and the System identity
is exempt from the "no partition, no write" guard, so **any readable first segment became a
System-owned `{segment}/_Activity/{id}` create.**

Measured on the public instance: `42P01: relation "whatsnew.activities" does not exist` for
`WhatsNew/_Activity/cc667f2e` (2026-09-18 15:35:29.421Z), reported as *"the WhatsNew namespace was
never provisioned"*. It was not a provisioning gap. The same pod logged
`MCP github_sync check failed for WhatsNew` **one millisecond later** (…29.422Z): an MCP caller
had asked to `check` the "Space" `WhatsNew`, which is no Space — it is the root-level declaration
node of the built-in `WhatsNew` NodeType, and What's New *entries* live under `Doc/WhatsNew/…` or
wherever a satellite files them (the feed lists by node TYPE). Three such calls in eight days are
the whole incident.

| Reading | Verdict |
|---|---|
| Provision a `whatsnew` schema (`OwnsPartition = true`, or a `PartitionDefinition`) | ❌ Makes a spurious write succeed, and mints a schema for a type declaration — by symmetry for ~30 sibling built-in types too. |
| Create the schema lazily at the write | ❌ Exactly what was removed on purpose: the storage router fails loudly (`42P01`) rather than conjuring a ghost schema for an arbitrary path segment (the 45-ghost-schema incident). **The `42P01` was the platform behaving as designed.** |
| Route the activity somewhere provisioned | ❌ Bakes in a home for a write that should not exist. |
| **Do not attempt the write** | ✅ The elevation's premise is checked where the elevation happens. |

On a **real** Space with no sync config the same hole was quieter and worse: a reader could plant a
System-owned activity node in somebody else's partition. The refusal covers that too, and an
unknown `sourceId` on a synced Space is refused the same way.

🚨 **How the writer was found, because the log line does not name it.**
`Unexpected error during node creation at {path}` carries no caller. Two things did: the **id
shape** — an 8-hex activity id is minted at exactly two code sites (`ActivityRunner.RunActivity`
here, `ContentIndexingActivity.Run` in MeshWeaver.Plugins), while script runs, test runs and chunk
builds use 32-hex and compiles use `compile-<ts>…` — and a **`Logs` action on the control
instance** for `WhatsNew` over the window, which put the tool's own warning beside the store's
error. Of the two 8-hex writers only GitSync elevates to System; the other runs as the caller and
is stopped by the write guard before it reaches the store.

🚨 **`ActivityRunner.RunActivity` does not provision anything, and neither does the create path.**
Its comment used to promise that a not-yet-provisioned partition "is fine —
`EnsurePartitionBootstrap` provisions + roots it". That stopped being true with #3451 (a *repair*
must never be able to create a partition — the heal writes at most a root row). A caller that
elevates to System is exempt from the write guard, so **it** owns establishing that its target is a
real partition before calling. Pinned by `ASyncTriggerNeedsASyncedSpaceTest`, which fails on the
in-memory store against the pre-fix code — *"expected a refusal, got activity
`WhatsNew/_Activity/4c37cc28`"* — so the defect is caught without Postgres.

### Open a pull request — AI drafts, you edit, then submit

This is a four-step flow, all in the **Pull request** section:

1. **AI drafts it.** Click **Draft pull request with AI**. The built-in
   `PullRequestWriter` agent is given the change context (the Space name + summary, the
   head and base branch) and returns a suggested **title** and **markdown body**. (If no
   model is configured, a sensible placeholder draft is created instead so you can still
   edit and submit.)
2. **A draft is created.** A `PullRequest` node is created at
   `{space}/_PullRequest/{id}` holding only **local draft state** — the suggested
   title/body and the head → base branches. It is not yet on GitHub.
3. **You edit it.** The title and body are shown in a data-bound editor wired **directly
   to that node** — your edits save as you type (no separate Save button). Tweak the
   wording, add detail, fix the branches.
4. **You submit it.** Click **Submit pull request** — this runs as an activity (progress +
   cancel, like every other operation above). The (edited) title/body are read from the
   node and a PR is opened on GitHub head → base. Only the **immutable handle** — the PR
   **number** and **URL** — is written back onto the node (that's how the Space later asks
   GitHub about this PR), and a clickable **link** (`#N ↗`) appears.

### Pull request status is read live — never replicated

A PR's lifecycle status (`Draft` → `Open` → `Merged` / `Closed`) is **owned by GitHub**.
The Space does **not** store it (a stored copy would drift). Click **Check status on
GitHub** to ask GitHub for the PR's *current* state on demand — the answer comes straight
from GitHub, so it can never be stale. The link (`#N ↗`) opens the pull request on
GitHub. Before a PR is opened it is simply a local **Draft**.

---

## 6. Issues & pull requests (browse and act)

Beyond moving content, a Space can **track and act on the repository's issues and pull
requests** — in the **Settings → GitHub Issues & PRs** tab. Structured lists are rendered
with the framework's data grid (never hand-built HTML).

### Issues — synced into the Space

Issues are the one GitHub object that is **materialized** into the mesh. Click **Sync
issues from GitHub** and every issue is mirrored to a node at `{space}/_Issue/{number}`
(NodeType `GitHubIssue`) and listed in a **live** table (number, title, state, author,
labels, comments, updated). The table binds to a synced query, so it refreshes itself as
issues land — from a sync, from **Create issue** (opens a new issue on GitHub and
materializes its node), or from a **webhook** (below). You can **create an issue** and
**comment** on one straight from the mesh — both act on GitHub, then refresh the affected
node. Programmatic: `IssueService.SyncIssues / SyncIssue / CreateIssue / CommentIssue /
WatchIssueNodes`, and the activity `hub.SyncIssuesFromGitHub(...)`.

### Pull requests — listed live, merge from the tab

The tab lists **every** pull request in the repo (number, title, author, status, draft,
head → base, updated), read **live** from GitHub (never persisted — a stored copy would
drift). **Refresh pull requests** re-reads them. You can **merge** an open PR — enter its
number and pick **Merge commit** or **Squash & merge** — which runs as a tracked activity.
Programmatic: `PullRequestService.ListAll / GetDetail / Comment / Merge`, and the activity
`hub.MergePullRequestOnGitHub(...)`. `GetDetail` additionally returns a **checks roll-up**
(CI pass/fail/pending over the head commit) and a **reviews roll-up** (latest decision per
reviewer).

> The AI-drafted **open a pull request** flow (draft → edit → submit) lives in the
> **GitHub Sync** tab (§5). This tab is for browsing and acting on issues + PRs that
> already exist on GitHub.

### Live updates via webhooks

So the synced issue nodes stay fresh without polling, register a **GitHub webhook** per
repo pointing at `https://{host}/webhooks/github` (content-type `application/json`),
subscribed to the **Issues** and **Issue comments** events, with the shared secret from
`GitHub:Webhook:Secret`. Each delivery is **HMAC-verified** (`X-Hub-Signature-256`) and
then applied: the event payload carries the full issue, so the receiver updates the
`{space}/_Issue/{number}` node of every Space that syncs that repo **without needing a
token** — the update runs under the system identity, merging in the new comment on a
comment event. Pull-request events are ignored (PR state is read live, so there is no node
to refresh). See `GitHubWebhookProcessor`.

#### 🚨 A RENAMED repository still matches — and a delivery that matches nothing SHOUTS

Matching a delivery to the Spaces that sync it is a comparison between two strings: the
`repositoryUrl` stored on each `{space}/_GitSync`, and the repository the payload is for.
**A GitHub rename breaks that comparison and nothing else.** The old url 301-redirects, so
git, `gh`, the REST API and every manual sync keep working — while a webhook payload always
carries the repository's *current* name, which the stored old name can never equal. Casing
is not the problem (`education` and `Education` are one repo, and always matched);
`education` versus `MeshWeaver.Education` is.

So when the stored strings match **nothing**, the receiver asks GitHub what each stored url
resolves to *today* (`GET /repos/{owner}/{repo}` follows the rename redirect and answers with
the repository's current `full_name`) and matches on that instead. The answer is cached per
repository for an hour, so this is a fallback and never a per-delivery network call: a
repository that was never renamed matches on the free string path and costs nothing. A config
matched this way is then **repointed** to the current url — keeping its scheme and host, so a
GitHub Enterprise config is never moved to github.com — which makes the repair permanent
instead of re-derived on every delivery.

And a delivery that still matches nothing is logged at **Warning**, naming both the incoming
repository and every repository it was compared against. That level is the point: a
zero-match means *every* Space that syncs that repository has just been skipped, which is a
stale config, a rename, or a hook on the wrong repository — each of which wants a human. At
Information it sits beside the routine "matched no sync source that needs updating" line and
is indistinguishable from a healthy mesh with nothing to do, which is how ten course Spaces
served four-day-stale content while every delivery reported success.

---

## 7. Operator setup (enabling the feature)

Server configuration for GitHub Sync — the first two are required, the rest optional:

1. **A GitHub OAuth App per portal host.** Register one under the GitHub organization
   (Settings → Developer settings → OAuth Apps → New). Set the **Authorization callback
   URL** to `https://{host}/connect/github/callback`. Copy the **Client ID** and generate
   a **Client Secret**. Request scope **`repo`** (read/write to private + public repos):

   ```jsonc
   // appsettings.json / env  (GitHub__OAuth__ClientId, GitHub__OAuth__ClientSecret)
   "GitHub": { "OAuth": { "ClientId": "Ov23li…", "ClientSecret": "<secret>", "Scopes": "repo" } }
   ```

   The **ClientId** is non-secret (env/values). Keep the **ClientSecret** in the **Key
   Vault** and surface it as the `GitHub__OAuth__ClientSecret` env via the
   SecretProviderClass (like the other secrets). Absent the client id + secret the Connect
   link is disabled and the rest of the tab still works for reading status.

2. **An encryption master key** so stored tokens are ciphertext at rest:

   ```jsonc
   "Ai": { "KeyProtection": { "MasterKey": "<base64 32-byte key>" } }
   ```

   This is the same key that protects AI provider credentials (see
   [AccessControl.md](/Doc/Architecture/AccessControl)). Without it, tokens are stored
   as plaintext (development only).

3. **A webhook secret (optional — for live issue updates).** To keep synced issue nodes
   fresh without polling, set a shared secret and register a webhook per repo (Issues +
   Issue comments events) at `https://{host}/webhooks/github`:

   ```jsonc
   "GitHub": { "Webhook": { "Secret": "<random shared secret>" } }
   ```

   Keep it in the Key Vault and surface it as `GitHub__Webhook__Secret`. Deliveries are
   HMAC-verified (`X-Hub-Signature-256`) against it — or against the instance-owned App's own
   webhook secret, which signs that App's `push` deliveries; absent both the endpoint logs a
   warning and returns **503**, and issue nodes refresh only on an explicit sync.

4. **A GitHub App (optional — machine identity for server-side sync).** Operations that run
   with no signed-in user — the plugin registry's sync of the plugins repo, boot imports —
   authenticate as a **GitHub App installation** rather than someone's personal OAuth token.
   The host binds `GitHub:App` next to `GitHub:OAuth`; left unconfigured, the instance's OWN App
   is used when its administrator connected one (Administration › Package sources › Connect
   GitHub — [Instance Repository Access](../InstanceRepositoryAccess)), and with neither,
   `GitHubSyncService.ResolveAuth` skips the App fallback and only user credentials work.

   ```jsonc
   "GitHub": { "App": {
     "ClientId": "Iv23li…",          // the App's client id
     "PrivateKey": "<PEM>",          // Key Vault → GitHub__App__PrivateKey
     "InstallationId": 12345678,     // or set InstallationOwner and let it resolve
     "InstallationOwner": "<org>"
   } }
   ```

   Note the transport split this enables: bulk push/fetch goes over the **git protocol**
   (`GitProtocolRepoClient`), because the REST path cost one request *per file* and a single
   large-repo sync exhausted the App installation's hourly rate budget. Refs, PRs and issues
   stay on the GitHub REST client (see "The GitHub REST transport" below).

   **Installation tokens live one hour, and the cache refreshes at subscription time.**
   `GitHubAppTokenService.GetInstallationToken()` returns a *deferred* observable: every
   subscription reads the current cached token, replays it while it is more than five minutes
   from expiry, and otherwise mints a replacement that concurrent subscribers share. That is
   the contract a long-lived consumer relies on — the Store's git poll loop holds ONE such
   observable for the life of its feed and subscribes once per pass. Before 2026-09-06 the
   promise was captured when the observable was *built*, so the refresh guard matched exactly
   once: the first expiry minted a new token, every later expiry compared against the stale
   capture and handed the expired token back. Two token lifetimes after boot every private
   source read as `401 Bad credentials` until the process restarted (Systemorph/Memex#165 —
   measured on the public instance as the first failure 2 h 01 min after the container started).
   `GitHubAppTokenRefreshTest` in `Memex.Portal.Shared.Test` holds the invariant with an
   injected clock: the second and third refresh mint, a fresh token replays.

   **🚨 A token that was never minted is a different event from one GitHub rejected — and
   they used to read the same.** Every fault out of `GetInstallationToken()` is now a
   `GitHubAppTokenMintException` carrying the stage it stopped at: `NotConfigured`,
   `Signing` (the key cannot sign — nothing reached GitHub), `InstallationDiscovery` (the App
   is not installed where it is expected), `TokenExchange` (GitHub refused the exchange — the
   nearest thing to "revoked"), `Response` (no token in the body) or `Transport` (no verdict
   at all), plus GitHub's status code where one exists. The translation is total — anything
   unexpected from the HTTP leaf is wrapped with the original as `InnerException`, and only a
   cancellation passes through as itself, because a cancelled mint is not a failed one. The
   type derives from `InvalidOperationException`, which is what all of these paths threw
   before, so every existing catch behaves identically, and it never carries the key, the JWT
   or the token.

   Why the distinction has to be carried by a TYPE rather than a message: a consumer may
   legitimately degrade to an anonymous fetch when no token can be minted — the Store's
   package feed does, because a public source must keep working — and the *next* thing that
   happens is GitHub refusing the private repositories, which the REST client reports as
   `GitHubAuthorizationException: Bad credentials`. That sentence is about a credential that was
   presented and judged. Read against a credential that was never issued it sends the reader
   to the installation's repository permissions, which are fine, and away from the private key
   or the installation, which are not (#4736 — four days of identical five-minute reports).
   `GitHubAppTokenMintFailureTest` pins both sides: a failed mint is a
   `GitHubAppTokenMintException` naming its stage, and a mint that SUCCEEDS and is then
   refused downstream is not one.

   Two things that follow, and neither is obvious from a log:

   - **An empty token means anonymous, not "a bad token".** The REST client sends no
     `Authorization` header at all for an empty string, so an anonymous read of a *private*
     repository answers **404**. A **401** on a
     private source therefore means a token was presented — the mint succeeded and the
     credential is the problem. The empty-token downgrade cannot produce a 401.
   - **The Store's own degradation is not in this repository.** `StoreManifestSource.Token()`
     is in-mesh C# in `Systemorph/MeshWeaver.Plugins` (`Store/Catalog/Source/`), so it
     compiles at runtime in the portal and never in core's CI. It catches the mint failure,
     names it at Error on the feed's own logger, and then lets the empty token flow on
     purpose.

All GitHub HTTP and serialization run through the controlled I/O pool — see
[ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling).

### The GitHub REST transport

Everything that is not bulk git transfer — repository lookup and creation, refs, the Git Data
calls of the REST push path, compare, issues, pull requests, reviews and check runs — goes
through `OctokitGitHubRepoClient` over `GitHubRestApi`: one `HttpClient`, System.Text.Json with
GitHub's snake_case names, and wire records holding exactly the fields the client reads or
writes. It replaced Octokit / Octokit.Reactive, and the platform no longer references Octokit at all (see "The transitional Octokit reference, removed" below); the type kept its name because
satellite repositories construct it, resolve it from DI and call `ParseRepoUrl` by it.

What the transport guarantees, because callers depend on it:

| Concern | Behaviour |
|---|---|
| Headers | `User-Agent: MeshWeaver`, `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`, `Authorization: token <token>`; **no** `Authorization` for an empty token (anonymous). |
| Redirects | Followed by the transport, up to four hops: the method is kept (a 303 becomes a body-less GET) and the credential is sent only to the configured API origin — scheme, host AND port compared, decided per request — so a redirect to `http://`, to another port or to another host, and an absolute pagination `Link` naming another host, all go without it. `HttpClient`'s own redirect following strips `Authorization` on every hop — a renamed private repository (GitHub answers its old name with a 301) would read as 404 — so the default client is built with `AllowAutoRedirect = false`, and DI constructs the client explicitly rather than letting a container inject an `HttpClient`. |
| Pagination | Listings (issues, pull requests, comments, reviews, check runs) request `per_page=100` and follow `Link: rel="next"` to the last page. Compare is one response, which is what the 300-file cap reasoning needs. |
| Branch lookup | `GET git/ref/heads/{branch}` — the exact-match endpoint. The plural `git/refs/…` Octokit called answers an array when the name is only a prefix of other branches. |
| Errors | A non-success status is a `GitHubApiException` carrying the status and GitHub's own `message`. A transport fault stays the `HttpRequestException` `HttpClient` threw. |

| Status | Exception |
|---|---|
| 401 | `GitHubAuthorizationException` |
| 403 | `GitHubRateLimitExceededException` when `x-ratelimit-remaining: 0` or the message names a rate limit; otherwise `GitHubForbiddenException` |
| 404 | `GitHubNotFoundException` |
| 422 | `GitHubValidationException` (with the raw `errors` array) |
| 429 | `GitHubRateLimitExceededException` |
| anything else (409 "Git Repository is empty.", 405 not mergeable, 5xx) | `GitHubApiException` with the status |

🚨 `GitHubApiException` deliberately does **not** derive from `HttpRequestException`: generic
classifiers treat that type as a transport fault worth retrying, and a GitHub 404 or 422 is an
answer, not a network failure — Octokit's exceptions never reached those classifiers either.
Classify by `StatusCode` or by the derived type, never by message text. The request shape of
every endpoint and every row of the mapping are pinned by `GitHubRestTransportTest`
(`MeshWeaver.Hosting.Test`) against a recorded-answer HTTP handler.

#### The transitional Octokit reference, removed

After the code port, `MeshWeaver.GitSync` kept a bare `<PackageReference Include="Octokit" />` and
`Directory.Packages.props` kept the pin for one cycle, so the image would keep carrying `Octokit.dll`.
Two consumers outside core still needed it:

- MeshWeaver.Plugins' Observability module named `Octokit.ApiException` and reached the DLL only
  through the image. A bundle's closure does not follow `MeshWeaver.*` references
  ([Module Closure Accounting](../ModuleClosureAccounting)), and the
  [module link gate](../ModulePlatformLinkGate) would not have held a roll for the missing assembly.
- A transitional internal overload, `IsMissingOrEmptyRepo(Octokit.ApiException)`, served Plugins'
  `EmptyRepoToleranceTest`.

Both conditions for removing it held before the reference, the overload and the pin went together:

1. MeshWeaver.Plugins#4206 merged, so nothing in Plugins names an Octokit type.
2. The Observability builds published after it are the ones instances run. The two
   package-update holds that could keep an older bundle, on `pearl` and `build`, were resolved
   when those instances rolled past the floor. A platform image without Octokit reaches an
   instance only through such a roll, and the same roll lifts a floor hold.

No satellite names an Octokit type. A repository-wide search of every satellite found only Memex's
own unused `PackageVersion`. If an Octokit type ever has to come back, it rides in the module that
names it, never in the platform.

---

## 8. What is and isn't synced

| Synced (export) | Not synced |
|---|---|
| Content nodes under the Space (markdown, typed, code), including nested folders | Satellites: `_Access`, `_Activity`, `_Thread`, `_Comment`, `_Notification`, `_PullRequest`, `_Issue` |
| The Space root (as `index.json`) | The GitHub credential (`{you}/_Provider/GitHub`) and the sync config (`{space}/_GitSync`) |
| | Nodes you marked to exclude from sync |

---

## See also

- [DataSyncSetup.md](/Doc/Architecture/DataSyncSetup) — the import-source model (platform content synced from a repo at a release tag).
- [StaticRepoImport.md](/Doc/Architecture/StaticRepoImport) — the import mechanism reused here (fingerprint, activity lock, upsert, prune).
- [What a Green Build Costs a Synced Space](/Doc/Architecture/GitSyncTriggerCost) — what one webhook delivery costs, the single field that makes it free, and why a source that never converges re-clones its repository on every green build.
- [When a Publication Seal Stops Advancing](/Doc/Architecture/PublicationSealStarvation) — why a green build can authorize an import that never happens, how to tell a HELD source from a settled one, and what a portal whose framework identity has stopped being published for looks like.
- [ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling) — why every GitHub HTTP call runs in the I/O pool.
- [AccessControl.md](/Doc/Architecture/AccessControl) — credential encryption + the master key.
