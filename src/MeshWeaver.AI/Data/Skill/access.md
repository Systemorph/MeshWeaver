---
nodeType: Skill
name: /access
description: Hand out access rights to mesh nodes — create the AccessAssignment the framework way (right namespace, right mainNode), pick the correct role and scope, grant platform admin correctly, and verify the grant took effect. Covers the Access Control UI, MCP recipes, and the pitfalls that make a grant silently do nothing.
icon: LockClosed
category: Skills
order: 11
---

You are handing out **access rights to mesh nodes**. Permissions in MeshWeaver are **data**:
an `AccessAssignment` MeshNode inside a `_Access` satellite namespace. There is no separate
ACL store — you grant access by creating a node with the right **placement** and **content**,
and the reactive `PermissionEvaluator` picks it up within ~1 second. Get the placement wrong
and the grant is **silently ignored** — the node exists, permissions don't change.

# The model — one node per subject per scope

```
path:        {scope}/_Access/{subject}_Access      ← MUST contain the /_Access/ segment
mainNode:    {scope}                                ← MUST equal the scope the path lives under
nodeType:    AccessAssignment
content:     { accessObject, displayName, roles: [ { role, denied? } ] }
```

- **`{scope}`** is the node/partition the grant covers: a partition root (`rbuergi`), a space
  (`ACME`), or any subtree (`ACME/Projects`). Grants **inherit downward** — a grant at `ACME`
  covers every node under `ACME/…`.
- **`accessObject`** is the subject's userId (matched against the login identity) or a Group id.
- **`roles[].role`**: `Admin` (all), `Editor` (read/create/update/comment), `Viewer` (read),
  `Commenter` (read/comment), or a custom `Role` node's id. `denied: true` turns a grant into
  a deny for that role at that scope (closest scope wins).
- **Satellites never get their own grants.** `_Thread`, `_Comment`, `_Activity`, … inherit from
  their `mainNode` automatically. Grant on the main node, never on a satellite path.

# Recipe 1 — grant a user a role on a node / space

Example: give `alice` Editor on the `ACME` space (works the same for any subtree path):

```bash
mcp create --node '{
  "id": "alice_Access",
  "namespace": "ACME/_Access",
  "name": "alice Access (Editor)",
  "nodeType": "AccessAssignment",
  "mainNode": "ACME",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "alice",
    "displayName": "Alice",
    "roles": [ { "$type": "RoleAssignment", "role": "Editor" } ]
  }
}'
```

If the subject already has an assignment at that scope, **update the existing node's `roles`**
instead of creating a second one — the convention is one node per subject per scope.

**GUI equivalent:** open the node → **Settings → Access Control** → *Add* row (or *Add
Assignment* dialog) → pick the subject in the **Subject (User or Group)** picker → pick the
role. The picker binds the canonical `AccessSubjectQueries` (users at the root namespace via
the `auth` mirror + groups in the scope's partition subtree) and filters in-memory,
diacritic-insensitively — "Burgi" finds "Bürgi". A person who has never logged in has no
`User` node yet and cannot be picked — see §not-yet-provisioned below.

# Recipe 2 — platform (global) admin: Admin partition, NEVER root

"Global admin" has exactly one shape: the `Admin` role **in the `Admin/_Access` namespace**
(`mainNode` empty). This makes the user a **platform admin** (invites, deletes, config —
checked via `hub.IsGlobalAdmin()`), NOT a data superuser:

```bash
mcp create --node '{
  "id": "alice_Access",
  "namespace": "Admin/_Access",
  "name": "alice — Admin",
  "nodeType": "AccessAssignment",
  "mainNode": "",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "alice",
    "displayName": "Alice",
    "roles": [ { "$type": "RoleAssignment", "role": "Admin" } ]
  }
}'
```

🚨 **Never create a grant in the root `_Access` namespace.** That is the data-superuser shape —
standing `Permission.All` over every partition — and is deliberately not how admins are
provisioned. Emergency cross-partition data access is an explicit break-glass elevation,
never a standing grant.

# Recipe 3 — public / anonymous read

- **All authenticated users**: `accessObject: "Public"` in `{scope}/_Access` (usually `Viewer`).
- **Not-logged-in visitors**: `accessObject: "Anonymous"`.
- **Whole-partition defaults** are better expressed as a `PartitionAccessPolicy` `_Policy` node
  (`publicRead: true` + write caps) than as Public grants on every subtree.

# Not-yet-provisioned users

A `User` node is created on first login/onboarding. If the person you want to grant to has no
`User` node yet, the subject picker cannot offer them. Options, in order:

1. **Invite them** (platform admin → Invitations) so onboarding creates the `User` node, then grant.
2. **Grant by principal anyway** via MCP (Recipe 1) — `accessObject` matches the userId at login
   time; the assignment simply lies dormant until they exist. Make sure the id you write is the
   exact login userId (email-derived), not a guessed display name.

# Verify — never declare a grant done without this

1. `mcp get @{scope}/_Access/{subject}_Access` → confirm the node exists AND `mainNode` == `{scope}`.
2. Confirm effect: have the user (or `search` under their identity) read a node under `{scope}` —
   the `Access denied` banner must be gone. Propagation is ~1 s; no restart, no cache flush.
3. For platform admin: the Global Administration tab appears on the user's profile.

# Pitfalls — each of these makes a grant silently do nothing

| Symptom | Cause |
|---|---|
| Assignment exists, still `Access denied` | `mainNode` empty (non-global grant) — the evaluator ignores it |
| Edited assignment has no effect | `mcp patch` does NOT write `mainNode` (indexed column) — use full `update` |
| Node created but never enforced | namespace doesn't end in `/_Access` — landed outside the security pipeline |
| Grant on a thread/comment has no effect | satellites inherit from `mainNode` — grant on the main node instead |
| "Global admin" can't see admin tabs | grant written to root `_Access` instead of `Admin/_Access` |
| User can't be picked in the GUI | no `User` node yet (never logged in) — invite first or grant by principal via MCP |

# Related

- [Granting Access via AccessAssignments](/Doc/Architecture/GrantingAccess) — field anatomy + full recipes
- [Access Control Architecture](/Doc/Architecture/AccessControl) — evaluator internals, roles, deny semantics
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — who a write runs as
