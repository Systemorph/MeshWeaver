---
NodeType: Markdown
Name: "Syncing a Space with GitHub"
Abstract: "How to connect a Space to a GitHub repository and move content both ways: sync FROM GitHub (import a repo to create a Space, update to latest, re-import at a commit) and sync TO GitHub (commit/export the Space's nodes as a single commit). Plus the repo operations: create a branch, commit, checkout / update to latest, and open a pull request (AI-drafted, you edit, then submit) with live PR status sync and a link. Covers the per-user OAuth connection, the Space settings, and how operators enable it."
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
| **Repository URL** | `https://github.com/owner/repo`. |
| **Branch** | The branch to commit to (default `main`). |
| **Create the branch if it doesn't exist** | When on, a missing branch is created as a fresh snapshot commit. |
| **Create the repository (private) if it doesn't exist** | When on, a missing repo is created **private** under the owner/org. |
| **Subdirectory** (optional) | Mirror the Space into this folder of the repo. Files outside it are left untouched. Empty = repository root. |

Click **Save repository settings**.

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

Import reuses the platform's content-addressed import pipeline (fingerprint gate +
activity lock + canonical upsert + prune) — see
[StaticRepoImport.md](/Doc/Architecture/StaticRepoImport).

> **Take-over edits survive.** A node you've edited and marked to exclude from sync is
> not overwritten or pruned by a re-import — that's how you "claim" content locally.

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

## 6. Operator setup (enabling the feature)

Two pieces of server configuration enable GitHub Sync:

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

All GitHub HTTP and serialization run through the controlled I/O pool — see
[ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling).

---

## 7. What is and isn't synced

| Synced (export) | Not synced |
|---|---|
| Content nodes under the Space (markdown, typed, code), including nested folders | Satellites: `_Access`, `_Activity`, `_Thread`, `_Comment`, `_Notification`, `_PullRequest` |
| The Space root (as `index.json`) | The GitHub credential (`{you}/_Provider/GitHub`) and the sync config (`{space}/_GitSync`) |
| | Nodes you marked to exclude from sync |

---

## See also

- [DataSyncSetup.md](/Doc/Architecture/DataSyncSetup) — the import-source model (platform content synced from a repo at a release tag).
- [StaticRepoImport.md](/Doc/Architecture/StaticRepoImport) — the import mechanism reused here (fingerprint, activity lock, upsert, prune).
- [ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling) — why every GitHub HTTP call runs in the I/O pool.
- [AccessControl.md](/Doc/Architecture/AccessControl) — credential encryption + the master key.
