---
title: Granting Access via AccessAssignments
order: 25
Description: "Grant user permissions in MeshWeaver by creating AccessAssignment MeshNodes in _Access satellite namespaces — field anatomy, three ready-to-run recipes, and common pitfalls."
---

# Granting Access via AccessAssignments

Permissions in MeshWeaver are **data** — they live as `AccessAssignment` MeshNodes inside `_Access` satellite namespaces. There is no dedicated admin UI. You grant access by creating an `AccessAssignment` node via MCP, a hub message, or the migration runner, and the `PermissionEvaluator` picks it up automatically via its synced query.

This page walks through the field anatomy and provides copy-paste recipes for the most common scenarios.

---

## Anatomy of an AccessAssignment

Every assignment is a MeshNode whose placement and content together determine what it grants.

| Field | Meaning |
|---|---|
| `path` | Where the assignment lives — must be **`{scope}/_Access/{id}`** (or `_Access/{id}` for global). |
| `mainNode` | The path the assignment scopes to. Must equal `{scope}` from the `path` above. |
| `accessObject` | The user or group this grants permissions to. Matched against `userId`. |
| `roles[].role` | The role names the user gets at this scope — typically `Admin` or `Editor`. |

> 🚨 **Both `path` (via the `/_Access/` segment) and `mainNode` matter.**  
> The `PermissionEvaluator`'s `SatelliteAccessRule` uses `mainNode` to decide which partition or subtree the assignment binds to. If `mainNode` is empty for a non-global assignment, the assignment is **silently ignored** and the user gets zero permissions.  
> **Symptom:** `Access denied: user 'X' lacks Read permission on '{scope}/Y'` even though an `AccessAssignment` exists at `{scope}/_Access/X_Access`.

> 🚨 **The namespace must end in `/_Access`** (or equal `_Access` exactly for global assignments).  
> The `PermissionEvaluator`'s synced query only routes namespaces that match this pattern, and nodes with an `_Access` segment land in the partition's `access` table. Place the node anywhere else and it never reaches the security pipeline.

---

## Recipe 1 — Grant a user Admin on their partition

This is the most common setup: giving a user Admin access over every node under their own partition (e.g. `rbuergi/...`).

**MCP (bash):**

```bash
mcp create --node '{
  "id": "rbuergi_Access",
  "namespace": "rbuergi/_Access",
  "name": "rbuergi Access",
  "nodeType": "AccessAssignment",
  "mainNode": "rbuergi",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "rbuergi",
    "displayName": "rbuergi",
    "roles": [ { "$type": "RoleAssignment", "role": "Admin" } ]
  }
}'
```

**C# / migration:**

```csharp
new MeshNode("rbuergi_Access", "rbuergi/_Access")
{
    Name = "rbuergi Access",
    NodeType = AccessAssignmentNodeType.NodeType,
    MainNode = "rbuergi",
    State = MeshNodeState.Active,
    Content = new AccessAssignment
    {
        AccessObject = "rbuergi",
        DisplayName  = "rbuergi",
        Roles = ImmutableList.Create(new RoleAssignment { Role = "Admin" })
    }
}
```

After the node is created, the `PermissionEvaluator`'s synced query picks it up within about one second. The `access_changed` Postgres trigger then rebuilds `partition_access` and `user_effective_permissions` automatically — no restart needed.

### Fixing an existing assignment with empty `mainNode`

If an assignment already exists but `mainNode` is empty (the node shows up in `mcp search nodeType:AccessAssignment` yet permissions still fail), patch it with a full update:

```bash
mcp update --nodes '[{
  "id": "rbuergi_Access",
  "namespace": "rbuergi/_Access",
  "path": "rbuergi/_Access/rbuergi_Access",
  "mainNode": "rbuergi",
  "name": "rbuergi Access",
  "nodeType": "AccessAssignment",
  "state": "Active",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "rbuergi",
    "displayName": "rbuergi",
    "roles": [ { "$type": "RoleAssignment", "role": "Admin" } ]
  }
}]'
```

> **Note:** `mcp patch` does **not** update `mainNode` — it is an indexed column and the patch operation ignores it. Always use `mcp update` with a full node body when you need to change `mainNode`.

---

## Recipe 2 — Grant a user Global Admin

Global Admin means Admin everywhere. The assignment lives at the root `_Access` namespace with no scope prefix, and `mainNode` is left empty.

```bash
mcp create --node '{
  "id": "rbuergi_GlobalAdmin",
  "namespace": "_Access",
  "name": "rbuergi Global Admin",
  "nodeType": "AccessAssignment",
  "mainNode": "",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "rbuergi",
    "displayName": "rbuergi",
    "roles": [ { "$type": "RoleAssignment", "role": "Admin" } ]
  }
}'
```

`SatelliteAccessRule` treats a root-level `Admin` role as Admin across the entire mesh.

---

## Recipe 3 — Grant another user access to your partition

Once you have Admin rights on a partition, you can extend access to other users. Here, `rbuergi` gives `alice` Editor rights on the `rbuergi` partition:

```bash
mcp create --node '{
  "id": "alice_Access",
  "namespace": "rbuergi/_Access",
  "name": "alice Access (Editor)",
  "nodeType": "AccessAssignment",
  "mainNode": "rbuergi",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "alice",
    "displayName": "Alice",
    "roles": [ { "$type": "RoleAssignment", "role": "Editor" } ]
  }
}'
```

The shape is identical to Recipe 1 — only `accessObject` and `displayName` differ.

---

## Verification

After creating or updating an assignment, run through this checklist:

1. `mcp search nodeType:AccessAssignment scope:descendants --basePath {scope}` — confirm the node landed in the right partition.
2. `mcp get @{scope}/_Access/{id}` — confirm `mainNode` is set correctly.
3. Refresh the user's home page in the portal — the `Activity` area should render its `MeshSearch` panels without an `Access denied` red banner.
4. **Optional SQL sanity check:**
   ```sql
   select * from "rbuergi".access where namespace = 'rbuergi/_Access';
   select * from public.user_effective_permissions where user_id = 'rbuergi';
   ```
   The first query should show the row; the second should show rebuilt permission rows for the partition.

---

## Common pitfalls

| Symptom | Likely cause |
|---|---|
| `Access denied` despite an AccessAssignment existing | `mainNode` is empty for a non-root assignment |
| Permissions still wrong after editing the assignment | Used `mcp patch` instead of `mcp update`; patch silently ignores `mainNode` |
| Search finds the assignment but it has no effect | Namespace doesn't end in `/_Access` — node landed in the wrong table |
| `Public→Admin` works but per-user denials fail in a test | Tests must use a per-user `accessObject`, not a Public assignment whose union bypasses negative-permission assertions |

---

## Source references

| File | Purpose |
|---|---|
| `src/MeshWeaver.Graph/Configuration/AccessAssignmentNodeType.cs` | NodeType definition and post-create handler that rebuilds permissions |
| `src/MeshWeaver.Graph/Security/RlsNodeValidator.cs` | Read-side enforcer that surfaces `Access denied` |
| `src/MeshWeaver.Hosting/Security/PermissionEvaluator.cs` | Synced query that aggregates AccessAssignments per user |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSchemaInitializer.cs` | `access_changed` trigger that rebuilds `partition_access` and `user_effective_permissions` |
