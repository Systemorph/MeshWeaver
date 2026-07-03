---
title: Granting Access via AccessAssignments
order: 25
Description: "Grant user permissions in MeshWeaver by creating AccessAssignment MeshNodes in _Access satellite namespaces — field anatomy, three ready-to-run recipes, and common pitfalls."
---

# Granting Access via AccessAssignments

Permissions in MeshWeaver are **data** — they live as `AccessAssignment` MeshNodes inside `_Access` satellite namespaces. You grant access either through the **Access Control UI** (Settings → Access Control on any node you administer) or by creating an `AccessAssignment` node via MCP, a hub message, or the migration runner. Either way, the `PermissionEvaluator` picks the node up automatically via its synced query — the UI is just a convenient writer of the same data.

This page walks through the UI, the field anatomy, and copy-paste recipes for the most common scenarios.

<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="20" y="20" width="160" height="44" rx="10" fill="#1e88e5"/>
  <text x="100" y="38" text-anchor="middle" fill="#fff" font-weight="bold">rbuergi/</text>
  <text x="100" y="56" text-anchor="middle" fill="#fff" font-size="11">Partition root</text>
  <rect x="20" y="120" width="160" height="44" rx="10" fill="#5c6bc0"/>
  <text x="100" y="138" text-anchor="middle" fill="#fff" font-weight="bold">rbuergi/_Access/</text>
  <text x="100" y="156" text-anchor="middle" fill="#fff" font-size="11">Satellite namespace</text>
  <line x1="100" y1="64" x2="100" y2="120" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="108" y="97" fill="currentColor" fill-opacity=".6" font-size="11">/_Access segment</text>
  <rect x="20" y="220" width="160" height="64" rx="10" fill="#8e24aa"/>
  <text x="100" y="240" text-anchor="middle" fill="#fff" font-weight="bold">AccessAssignment</text>
  <text x="100" y="256" text-anchor="middle" fill="#fff" font-size="11">accessObject: rbuergi</text>
  <text x="100" y="272" text-anchor="middle" fill="#fff" font-size="11">mainNode: rbuergi</text>
  <text x="100" y="288" text-anchor="middle" fill="#fff" font-size="11">roles: [Admin]</text>
  <line x1="100" y1="164" x2="100" y2="220" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="108" y="197" fill="currentColor" fill-opacity=".6" font-size="11">MeshNode at {id}</text>
  <rect x="300" y="120" width="180" height="64" rx="10" fill="#26a69a"/>
  <text x="390" y="140" text-anchor="middle" fill="#fff" font-weight="bold">PermissionEvaluator</text>
  <text x="390" y="158" text-anchor="middle" fill="#fff" font-size="11">synced query on</text>
  <text x="390" y="174" text-anchor="middle" fill="#fff" font-size="11">nodeType:AccessAssignment</text>
  <line x1="180" y1="248" x2="300" y2="160" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="225" y="192" fill="currentColor" fill-opacity=".6" font-size="11" transform="rotate(-25,225,192)">~1s pickup</text>
  <rect x="300" y="240" width="180" height="44" rx="10" fill="#43a047"/>
  <text x="390" y="258" text-anchor="middle" fill="#fff" font-weight="bold">Postgres triggers</text>
  <text x="390" y="276" text-anchor="middle" fill="#fff" font-size="11">rebuild effective perms</text>
  <line x1="390" y1="184" x2="390" y2="240" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="560" y="120" width="160" height="64" rx="10" fill="#f57c00"/>
  <text x="640" y="140" text-anchor="middle" fill="#fff" font-weight="bold">SatelliteAccessRule</text>
  <text x="640" y="158" text-anchor="middle" fill="#fff" font-size="11">checks mainNode</text>
  <text x="640" y="174" text-anchor="middle" fill="#fff" font-size="11">to scope permission</text>
  <line x1="480" y1="152" x2="560" y2="152" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="560" y="240" width="160" height="44" rx="10" fill="#e53935"/>
  <text x="640" y="258" text-anchor="middle" fill="#fff" font-weight="bold">user_effective_permissions</text>
  <text x="640" y="276" text-anchor="middle" fill="#fff" font-size="11">ready for read requests</text>
  <line x1="480" y1="270" x2="560" y2="262" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
</svg>

*AccessAssignment nodes live in `_Access` satellite namespaces; `PermissionEvaluator` picks them up via a synced query and Postgres triggers rebuild effective permissions automatically.*

---

## The Access Control UI

Open a node you administer → **Settings → Access Control**. The page shows the assignments inherited from the parent scope (read-only), the editable assignments at the current scope, an inline **Add** row, and a collapsed **Advanced** section for the partition policy.

The **Subject (User or Group)** picker is bound to the canonical queries in `AccessSubjectQueries` (`src/MeshWeaver.Mesh.Contract/Security/AccessSubjectQueries.cs`):

- **Users** — `nodeType:User namespace:""`. Users live at the ROOT namespace (path = userId); the path-less query is pinned to the central `auth` lookup mirror by `UserNodeType`'s routing rule, so one query covers every user in the mesh. 🚨 The legacy `namespace:User` shape targets the pre-V27 `user` schema, which no longer exists — it silently returns **zero** rows. Never hand-roll subject queries; reference `AccessSubjectQueries`.
- **Groups** — `nodeType:Group namespace:{partition} scope:subtree`: every group defined in the scope's partition.

The picker loads the (bounded) subject set once and filters **in-memory, diacritic- and case-insensitively** (`SearchText.Fold`): typing "Burgi" finds "Bürgi".

**Limitation — not-yet-provisioned users.** A `User` node is created at first login/onboarding, so a person who has never logged in cannot be picked. Either invite them first (platform admin → Invitations), or grant by principal via MCP (Recipe 3 below with the exact login userId) — the assignment lies dormant until they exist.

---

## Anatomy of an AccessAssignment

Every assignment is a MeshNode whose placement and content together determine what it grants.

| Field | Meaning |
|---|---|
| `path` | Where the assignment lives — must be **`{scope}/_Access/{id}`** (`Admin/_Access/{id}` for platform admins — see Recipe 2). |
| `mainNode` | The path the assignment scopes to. Must equal `{scope}` from the `path` above. |
| `accessObject` | The user or group this grants permissions to. Matched against `userId`. |
| `roles[].role` | The role names the user gets at this scope — typically `Admin` or `Editor`. |

> 🚨 **Both `path` (via the `/_Access/` segment) and `mainNode` matter.**  
> The `PermissionEvaluator`'s `SatelliteAccessRule` uses `mainNode` to decide which partition or subtree the assignment binds to. If `mainNode` is empty for a non-global assignment, the assignment is **silently ignored** and the user gets zero permissions.  
> **Symptom:** `Access denied: user 'X' lacks Read permission on '{scope}/Y'` even though an `AccessAssignment` exists at `{scope}/_Access/X_Access`.

> 🚨 **The namespace must end in `/_Access`.**  
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

## Recipe 2 — Grant a user Global (Platform) Admin

Global/platform admin has **one** canonical shape: an `AccessAssignment` granting the `Admin` role **on the Admin partition** — namespace `Admin/_Access`, `mainNode` left empty. This is exactly what `GlobalAdminSeed` (config-driven admins via `Auth:GlobalAdmins`) and `UserOnboardingService.GrantPlatformAdmin` (first user) write, and what `hub.IsGlobalAdmin()` reads.

```bash
mcp create --node '{
  "id": "rbuergi_Access",
  "namespace": "Admin/_Access",
  "name": "rbuergi — Admin",
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

`PermissionEvaluator`'s global-admin short-circuit turns `Permission.All` at scope `Admin` into the platform-admin gates (`hub.IsGlobalAdmin()`, admin tabs, invites, config).

> 🚨 **Never grant at the root `_Access` namespace.** A root-level `Admin` assignment is the *data-superuser* shape — standing `Permission.All` on every partition's data — and is deliberately **not** how platform admins are provisioned. Platform admins manage the platform; emergency cross-partition data access goes through explicit elevation (break-glass), never a standing grant. See [AccessControl](/Doc/Architecture/AccessControl) → "The Admin partition".

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
