---
Name: Platform admins can always see and delete a Space's GitHub sync config
Category: Fix
Description: A platform administrator can now read and delete any Space's GitHub sync config, and see the root of a system-owned Space — even when the Space was created by the platform itself and carries no grant a person could hold. Until now such a config answered "Not found" and "Delete permission denied" to the very operator who had to remove it.
Icon: Checkmark
Order: -20260912
---

# Platform admins can always see and delete a Space's GitHub sync config

A Space that syncs **one-way** from a GitHub repository is **system-owned**: the repository rewrites
it on every sync, so nobody but the importer may write it, and the platform refuses — and retracts —
every Admin or Editor grant on it. That rule is right, and it has one blind spot: a Space the
**platform itself** created.

On 2026-09-12 memex.meshweaver.cloud carried exactly that: `MeshWeaver/_GitSync`, wired by the
platform in a Space owned by `system-security`, importing the **entire** core repository on every
green build — hundreds of refused nodes per pass, and pods at 9.9 GiB. The platform administrator
tried to delete it and got:

```
get    MeshWeaver/_GitSync   →   Not found
delete MeshWeaver/_GitSync   →   Delete permission denied for 'MeshWeaver/_GitSync'
```

He could not even see that the Space existed, while the webhook log listed the config among the
mesh's seventy sync configs. A platform admin is deliberately **not a data superuser** — the
Admin-partition grant confers no Read on anybody's Space — and on a system-owned Space there is no
grant anyone *could* hold. The config was unremovable through any API.

## What changed

Two node types now carry their own access rule, with the same second leg the sync **triggers** have
had since August (triggering a sync is a platform action, so a platform admin may always do it):

- **`GitHubSyncConfig`** (`{space}/_GitSync`) — a platform admin can always **read** and **delete**
  it, on every Space. Create and Update are unchanged: an admin still cannot repoint somebody's sync.
- **`Space`** — a platform admin can **read the root node** of a Space *while it is system-owned*.
  Its content stays gated; Update and Delete are unchanged.

For everybody else nothing moves: the ordinary check is byte-for-byte what it was, and the permission
fold itself is untouched — the widening comes from the node type's rule, which every seam (the RLS
read filter, the delivery gate, the delete pre-flight) consults through one gate.

A sync config carries the repository, the branch and the last-sync state — never a credential; that
lives in a separate node in the owner's own partition. Deleting the **Space** of a system-owned
partition is deliberately not widened: a paid plugin's Space is system-owned too, and its
entitlement grants would go with it.

Documented under [Access Control](/Doc/Architecture/AccessControl) → "The two type-scoped exceptions".
