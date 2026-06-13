---
NodeType: Markdown
Name: "Syncing a Space with GitHub"
Abstract: "How to connect a Space to a GitHub repository and move content both ways: export ('sync back') the Space's nodes into a repo as a single commit, and import a repo to create a new Space or re-import an existing one at any commit. Covers the per-user OAuth connection, the Space settings, branch / create-repo options, the stored synced commit, and how operators enable it."
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

- **Export ("sync back")** — write the Space's nodes into the repo as a single
  commit. The repo mirrors the Space subtree.
- **Import** — read a repo to **create a new Space**, or **re-import** an existing
  Space at any branch or commit (bringing it to that state).

Everything is configured in the Space's **Settings → GitHub Sync** tab. Your GitHub
connection is **personal**: you authorize once with your own GitHub account, the
commit is authored as you, and your token never leaves your account.

For the underlying, fingerprint-gated *import-source* model (platform content synced
from a repo at a release tag), see [DataSyncSetup.md](/Doc/Architecture/DataSyncSetup).

---

## 1. Connect your GitHub account (once)

GitHub Sync authenticates with a **long-standing OAuth credential** obtained through
GitHub's **device flow** — no password, no pasted personal access token.

1. Open any Space → **Settings → GitHub Sync**.
2. Under **Your GitHub account**, click **Connect GitHub**.
3. A code and a link (`https://github.com/login/device`) appear. Open the link in
   your browser, enter the code, and approve.
4. The tab flips to **✓ Connected as _your-login_**. You can **Disconnect** anytime.

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

## 3. Export — "Sync now"

Click **Sync now**. The Space's content nodes are serialized and pushed as **one
commit**:

- Markdown pages → `*.md` (with YAML frontmatter); typed nodes → `*.json`; code → `*.cs`.
- A node that has children is written as `Name/index.md` so its children live in the
  `Name/` folder; the Space itself is the top-level `index.json`.
- **Satellites and secrets are never exported** — access assignments, threads,
  activities, notifications, the GitHub credential, and the sync config are all skipped.
- **Mirror semantics:** within the configured subdirectory the repo is made to match
  the Space exactly — nodes you deleted are removed from the repo. Anything outside the
  subdirectory is untouched.

When it finishes, the resulting **commit SHA is stored on the Space** and shown as
*"Last synced: … — commit …"*.

---

## 4. Import & re-import

**Re-import an existing Space at a chosen commit.** Under **Sync**, the **Commit or
branch to import** field is pre-filled with the last synced commit. Change it to any
commit SHA or branch and click **Re-import at this commit** — the Space is mirrored to
that state (added/updated/removed to match), and the new commit is recorded. This is
how you roll a Space forward or back to a specific repository state.

**Create a new Space from a repository.** Importing a repo into a brand-new Space
provisions the partition, makes you its admin, and imports every node. (Programmatic
entry point: `GitHubSyncService.ImportFromGitHub(repoUrl, commitish, newSpaceId, name,
subdirectory, userId)`.)

Import reuses the platform's content-addressed import pipeline (fingerprint gate +
activity lock + canonical upsert + prune) — see
[StaticRepoImport.md](/Doc/Architecture/StaticRepoImport).

> **Take-over edits survive.** A node you've edited and marked to exclude from sync is
> not overwritten or pruned by a re-import — that's how you "claim" content locally.

---

## 5. Operator setup (enabling the feature)

Two pieces of server configuration enable GitHub Sync:

1. **A GitHub OAuth App with the device flow enabled.** Register one under the GitHub
   organization (Settings → Developer settings → OAuth Apps → *Enable Device Flow*).
   Request scope **`repo`** (read/write to private + public repos). Then configure its
   client id:

   ```jsonc
   // appsettings.json
   "GitHub": { "OAuth": { "ClientId": "Iv1.xxxxxxxxxxxxxxxx", "Scopes": "repo" } }
   ```

   No client secret is needed for the device authorization grant. Absent a client id the
   Connect button is disabled and the rest of the tab still works for reading status.

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

## 6. What is and isn't synced

| Synced (export) | Not synced |
|---|---|
| Content nodes under the Space (markdown, typed, code), including nested folders | Satellites: `_Access`, `_Activity`, `_Thread`, `_Comment`, `_Notification` |
| The Space root (as `index.json`) | The GitHub credential (`{you}/_Provider/GitHub`) and the sync config (`{space}/_GitSync`) |
| | Nodes you marked to exclude from sync |

---

## See also

- [DataSyncSetup.md](/Doc/Architecture/DataSyncSetup) — the import-source model (platform content synced from a repo at a release tag).
- [StaticRepoImport.md](/Doc/Architecture/StaticRepoImport) — the import mechanism reused here (fingerprint, activity lock, upsert, prune).
- [ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling) — why every GitHub HTTP call runs in the I/O pool.
- [AccessControl.md](/Doc/Architecture/AccessControl) — credential encryption + the master key.
