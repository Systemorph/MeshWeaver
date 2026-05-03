---
title: Granting Access via AccessAssignments
order: 25
---

# Granting Access via AccessAssignments

Permissions in MeshWeaver are data — they live as `AccessAssignment` MeshNodes
in `_Access` satellite namespaces. There is no admin UI; you grant access by
*creating an AccessAssignment node* (via MCP, a hub message, or the migration
runner) and the SecurityService picks it up via its synced query.

This page is the recipe.

## Anatomy of an AccessAssignment

Three fields decide what the assignment *grants*:

| Field            | Meaning                                                                  |
|------------------|--------------------------------------------------------------------------|
| `path`           | Where the assignment lives — must be **`{scope}/_Access/{id}`** (or `_Access/{id}` for global). |
| `mainNode`       | The path the assignment scopes to. Must equal `{scope}` from above.       |
| `accessObject`   | The user (or group) this grants permissions to. Match against `userId`.   |
| `roles[].role`   | The role names the user gets at this scope (typically `Admin` or `Editor`). |

🚨 **Both `path` (via the `/_Access/` segment) and `mainNode` matter.** The
SecurityService's `SatelliteAccessRule` uses `mainNode` to decide which
partition / subtree the assignment binds to. If `mainNode` is empty for a
non-global assignment, the assignment is silently ignored and the user gets
zero permissions. Symptom: `Access denied: user 'X' lacks Read permission on
'{scope}/Y'` even though the AccessAssignment exists at `{scope}/_Access/X_Access`.

🚨 **The namespace must end in `/_Access`** (or equal `_Access` globally).
SecurityService's synced query routes only those, and a path containing
`_Access` segment lands in the partition's `access` table. Anywhere else and
the assignment never reaches the security pipeline.

## Recipe 1 — grant a user Admin on a partition

For example, granting `rbuergi` Admin on the entire `rbuergi` partition (his
home — every node under `rbuergi/...`):

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

Or as a `MeshNode` in C# / a migration:

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

After the create, the SecurityService's synced query picks the new node up
within ~1 second and `partition_access` + `user_effective_permissions` rebuild
via the `access_changed` Postgres trigger. No restart needed.

### Existing assignment with empty `mainNode`

If a partition's AccessAssignment already exists but has empty `mainNode`
(symptom: assignment shows up in `mcp search nodeType:AccessAssignment` but
permissions still fail), patch it:

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

`mcp patch` does not update `mainNode` (it's part of the indexed columns and
patching ignores unknown fields). Use `mcp update` with a full node body.

## Recipe 2 — grant a user Global Admin

Global Admin = Admin everywhere. The assignment lives at the root `_Access`
namespace (no scope prefix), `mainNode` empty:

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

This grants `rbuergi` the `Admin` role at the root, which `SatelliteAccessRule`
treats as Admin everywhere.

## Recipe 3 — grant another user access to my partition

Once you're Admin on a partition, you can grant other users access to it.
For example, `rbuergi` gives `alice` Editor access on `rbuergi`:

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

Same shape, different `accessObject`.

## Verification

After creating the assignment:

1. `mcp search nodeType:AccessAssignment scope:descendants --basePath {scope}` —
   confirm it landed in the right partition.
2. `mcp get @{scope}/_Access/{id}` — confirm `mainNode` is set correctly.
3. Refresh the user's home page in the portal — the `Activity` area should
   render its `MeshSearch` panels without the `Access denied` red banner.
4. Optional sanity check: `select * from "rbuergi".access where namespace =
   'rbuergi/_Access';` in psql shows the row, and
   `select * from public.user_effective_permissions where user_id = 'rbuergi';`
   shows rebuilt rows for the partition.

## Common pitfalls

| Symptom | Likely cause |
|---------|--------------|
| `Access denied` despite AccessAssignment existing | `mainNode` empty for a non-root assignment |
| Permissions still wrong after edit | Used `mcp patch` instead of `mcp update`; patch ignores `mainNode` |
| Search finds the assignment but it does nothing | Namespace doesn't end in `/_Access` — assignment lives in the wrong table |
| Public→Admin works but per-user denies fail in a test | Tests must use per-user `accessObject`, not a Public assignment whose union bypasses negative tests |

## Source links

- `src/MeshWeaver.Graph/Configuration/AccessAssignmentNodeType.cs` — the NodeType definition + post-create handler that rebuilds permissions.
- `src/MeshWeaver.Graph/Security/RlsNodeValidator.cs` — the read-side enforcer that surfaces `Access denied`.
- `src/MeshWeaver.Hosting/Security/SecurityService.cs` — the synced query that aggregates AccessAssignments per user.
- `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSchemaInitializer.cs` — the `access_changed` trigger that rebuilds `partition_access` + `user_effective_permissions` after every assignment write.
