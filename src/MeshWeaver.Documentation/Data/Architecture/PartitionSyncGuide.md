---
Name: Managing Partition Sync (Admin Guide)
Category: Architecture
Description: A platform-admin how-to for partition sync — what "Synced" vs "Not synced" means, the "we initialize a default, you bring your own" workflow (e.g. seed an API key, then the customer replaces it and it sticks), and how to change a partition's sync status from the Admin → Partitions screen.
Icon: DatabaseCog
---

# Managing Partition Sync (Admin Guide)

This is the **task-oriented** companion to the architecture doc [Static-Repo Import](/Doc/Architecture/StaticRepoImport). If you just want to know *which button to press to stop a partition from being overwritten*, you're in the right place.

## What "sync" means

Some partitions ship **seeded from a built-in source** — the documentation (`Doc`), the built-in agents (`Agent`), the AI model catalog (`Provider`). On every deployment the platform re-materialises that source into the partition so the shipped content stays current. That's **sync**.

The catch: if sync keeps overwriting a partition, **your edits to it can be reset on the next update**. The control that decides this is each partition's **sync status**.

## The two sync statuses

| Status | What it does | Use it when |
|---|---|---|
| **Synced** *(default)* | The partition is kept up to date from its built-in source. Your edits to synced nodes **may be overwritten** when the source changes (e.g. a new release). | You want to keep receiving the shipped content/updates. |
| **Not synced** *(a.k.a. "sync: none", decoupled)* | The partition is **fully owned by your data**. The built-in source **never touches it again** — your edits persist across every deployment. | You've customised the partition and want your values to stick. |

Under the hood "Not synced" sets the partition root's `SyncBehavior` to `ExcludeThisAndChildren`, which the importer skips — root **and** every child. See [Static-Repo Import → Decoupling a partition](/Doc/Architecture/StaticRepoImport).

## The common workflow: "we initialize a default, you bring your own"

This is the pattern for anything we ship with a **placeholder/shared default** that a customer is expected to **replace with their own** — most importantly **API keys**.

1. **We initialize.** The partition ships **Synced**, seeded with a working default (e.g. a shared platform key, or an empty placeholder). Everything works out of the box.
2. **You bring your own.** A platform admin enters the customer's own value — e.g. **their own Anthropic API key** — through the GUI.
3. **You decouple.** Set the partition to **Not synced**. From this moment your value is **permanent**: no deployment, migration, or content update will ever reset it back to our default.

> If you skip step 3, a later update can re-seed the partition and **overwrite your key with our default** — which is exactly the incident this feature was built to prevent (the `Provider/Anthropic` key silently reverting to the shared Azure key, 2026-06-25).

### Worked example — the Anthropic provider key

- **Initialized:** `Provider/Anthropic` shipped Synced with a shared **Azure AI Foundry** key (endpoint `…services.ai.azure.com/anthropic`).
- **Bring your own:** the admin opens the provider, clicks **Enter Key**, and pastes the customer's **direct Anthropic** key (`sk-ant-…`, endpoint `api.anthropic.com`). It is stored **encrypted at rest** and never displayed.
- **Decouple:** the admin sets the `Provider` partition to **Not synced**.
- **Result:** the customer's key is now the permanent provider credential. Re-deploys and catalog updates leave it untouched.

## How to change a partition's sync status

**Platform admins only.**

### From the Admin → Partitions screen (recommended)

1. Open **Admin → Partitions**. You'll see every partition with its current **sync status** and its **source**.
2. Click the partition (e.g. `Provider`) to open its full-screen detail.
3. Switch **Sync status** to **Not synced** (or back to **Synced** to re-enable updates).

The change takes effect on the next sync pass — your decoupled partition is skipped from then on.

### Per node (finer control)

You don't have to decouple a whole partition. The **Stop Sync** toggle on an individual node decouples just that node (and, for a subtree claim, its children) while the rest of the partition keeps syncing.

## Caveats — read before you change anything

- **To stop sync, set "Not synced" — never try to "remove the source".** Deleting/unregistering a partition's source is **destructive**: the platform treats the partition as orphaned and **deletes it entirely** (all nodes, including your keys). The safe, reversible control is the **sync status**.
- **"Not synced" is reversible.** Flip it back to **Synced** and the partition resumes receiving source updates (which can overwrite local edits again).
- **Encrypted secrets stay encrypted.** Decoupling does not change how secrets are stored; keys remain encrypted at rest regardless of sync status.

## Related

- [Static-Repo Import](/Doc/Architecture/StaticRepoImport) — the architecture: how sources are materialised, the content-addressed activity, and the decouple internals.
- [CQRS & Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the sync-status read must be authoritative.
- [Access Control](/Doc/Architecture/AccessControl) — what "platform admin" means.
