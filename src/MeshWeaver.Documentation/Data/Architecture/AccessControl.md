---
Name: Access Control Architecture
Category: Documentation
Description: How MeshWeaver implements row-level security through AccessAssignment MeshNodes and hierarchical permission evaluation
Icon: /static/DocContent/Architecture/AccessControl/icon.svg
---

MeshWeaver provides row-level security through **AccessAssignment MeshNodes** stored directly in the mesh node hierarchy. Permissions are evaluated by walking the node tree from root to target path, applying closest-wins semantics.

# Core Concepts

## AccessAssignment MeshNodes

Access control is managed through AccessAssignment nodes — first-class MeshNodes with `nodeType: "AccessAssignment"`. Each assignment grants (or denies) a role to a subject at a specific scope.

AccessAssignment nodes are **satellite entities** stored in the `_Access` sub-namespace:

```
Node path: {scope}/_Access/{Subject}_Access
Node type: AccessAssignment
Content: {
  "accessObject": "Alice",
  "displayName": "Alice Chen",
  "roles": [
    { "role": "Editor" },
    { "role": "Viewer" }
  ]
}
```

On disk (file system persistence), access files live in `_Access/` sub-directories:
```
ACME/
  _Access/
    Public_Access.json     ← All authenticated users get Viewer
    Alice_Access.json      ← Alice gets Editor
  Projects/
    _Access/
      Bob_Access.json      ← Bob gets Viewer on ACME/Projects
```

In PostgreSQL, access nodes are routed to a dedicated `access` table (via `PartitionDefinition.StandardTableMappings`), separate from the main `mesh_nodes` table.

Each AccessAssignment node maps **one subject** (User or Group) to **multiple roles** at a given scope. This reduces the number of nodes and trigger invocations compared to one-node-per-role.

**Key properties:**

| Property | Description |
|----------|-------------|
| `AccessObject` | User or Group identifier |
| `DisplayName` | Optional display name for the subject |
| `Roles` | Array of `RoleAssignment` entries |
| `Roles[].Role` | Role to grant/deny (Admin, Editor, Viewer, Commenter, or custom) |
| `Roles[].Denied` | If true, denies the role instead of granting it |

## Built-in Roles

| Role | Permissions | Flag Value |
|------|------------|------------|
| Admin | Read, Create, Update, Delete, Comment | 31 (All) |
| Editor | Read, Create, Update, Comment | 23 |
| Viewer | Read | 1 |
| Commenter | Read, Comment | 17 |

## Permission Flags

```csharp
[Flags]
public enum Permission
{
    None    = 0,
    Read    = 1,
    Create  = 2,
    Update  = 4,
    Delete  = 8,
    Comment = 16,
    All     = Read | Create | Update | Delete | Comment
}
```

# Permission Evaluation

Permissions are evaluated by walking AccessAssignment nodes from the global scope through each ancestor down to the target path.

## Scope Hierarchy

For a target path `ACME/Project/Task1`, the evaluation order is:

```
"" (global) → "ACME" → "ACME/Project" → "ACME/Project/Task1"
```

At each scope level, the system collects AccessAssignment MeshNodes and applies **closest-wins** semantics: a deeper assignment for the same role overrides a shallower one.

## Evaluation Flow

```mermaid
sequenceDiagram
    participant Client
    participant SecurityService
    participant Persistence
    Client->>SecurityService: GetEffectivePermissionsAsync("ACME/Project", "Alice")
    SecurityService->>Persistence: GetChildrenAsync("") [global scope]
    Persistence-->>SecurityService: AccessAssignment nodes
    SecurityService->>Persistence: GetChildrenAsync("ACME")
    Persistence-->>SecurityService: AccessAssignment nodes
    SecurityService->>Persistence: GetChildrenAsync("ACME/Project")
    Persistence-->>SecurityService: AccessAssignment nodes
    SecurityService->>SecurityService: Resolve roles (closest-wins)
    SecurityService->>SecurityService: Combine permissions
    SecurityService-->>Client: Permission.Read | Permission.Create | Permission.Update | Permission.Comment
```

## Closest-Wins Semantics

When the same role is assigned at multiple levels, the deepest (closest to target) assignment wins:

| Scope | Assignment | Effect |
|-------|-----------|--------|
| `""` (global) | Alice: Admin | Grants All permissions globally |
| `ACME` | Alice: Admin (Denied) | **Overrides** global grant — no Admin at ACME |
| `ACME/Project` | Alice: Editor | Grants Editor at ACME/Project |

At `ACME/Project`, Alice has Editor permissions (Read + Create + Update + Comment) but not Admin.

## Deny Override

A deny assignment blocks an inherited grant for a specific role, but does not affect other roles. Each node's `Roles[]` array can mix grants and denies:

```
Global:      Alice_Access → roles: [{ role: "Admin" }]
ACME:        Alice_Access → roles: [{ role: "Editor" }]
ACME/Secure: Alice_Access → roles: [{ role: "Admin", denied: true }]
```

At `ACME/Secure`, Alice has Editor permissions (from ACME, inherited) but not Admin (denied at ACME/Secure).

# Node Type Architecture

Access control uses these shipped node types:

## AccessAssignment
- **NodeType**: `"AccessAssignment"`
- **Content**: `AccessAssignment` record with `Id` and `Roles[]` array
- **Path pattern**: `{scope}/_Access/{Subject}_Access`
- **Name pattern**: `{Subject} Access`
- Created via `ISecurityService.AddUserRoleAsync()` or `IMeshCatalog.CreateNodeAsync()`
- One node per subject per scope — multiple roles are stored in the `Roles` array

## User
- **NodeType**: `"User"`
- **Content**: `AccessObject` record (Id, Name, Description, Icon)
- Used as subjects in AccessAssignment nodes

## Group
- **NodeType**: `"Group"`
- **Content**: `AccessObject` record
- Contains GroupMembership child nodes for members
- Groups can be nested (a group member can be another group)

## GroupMembership
- **NodeType**: `"GroupMembership"`
- **Content**: `GroupMembership` record (`Member`, `DisplayName`, `Groups[]`)
- **Path pattern**: `{Scope}/{Member}_Membership`
- Maps one member (User or Group) to one or more groups at a given scope
- Mirrors the AccessAssignment 1:1 pattern (one node per member per scope)
- `Groups[]` contains `MembershipEntry` records with a `Group` property

## Role
- **NodeType**: `"Role"`
- **Content**: `Role` record (Id, DisplayName, Permissions, IsInheritable)
- Custom roles extend the built-in set

# ISecurityService API

```csharp
public interface ISecurityService
{
    // Permission evaluation
    Task<bool> HasPermissionAsync(string nodePath, Permission permission, CancellationToken ct);
    Task<bool> HasPermissionAsync(string nodePath, string userId, Permission permission, CancellationToken ct);
    Task<Permission> GetEffectivePermissionsAsync(string nodePath, CancellationToken ct);
    Task<Permission> GetEffectivePermissionsAsync(string nodePath, string userId, CancellationToken ct);

    // Role management
    Task<Role?> GetRoleAsync(string roleId, CancellationToken ct);
    IAsyncEnumerable<Role> GetRolesAsync(CancellationToken ct);
    Task SaveRoleAsync(Role role, CancellationToken ct);

    // Convenience methods (create/delete AccessAssignment MeshNodes)
    Task AddUserRoleAsync(string userId, string roleId, string? targetNamespace, string? assignedBy, CancellationToken ct);
    Task RemoveUserRoleAsync(string userId, string roleId, string? targetNamespace, CancellationToken ct);
}
```

# Anonymous and Public Access

MeshWeaver distinguishes between two well-known user groups:

| User | Constant | Meaning |
|------|----------|---------|
| **Anonymous** | `WellKnownUsers.Anonymous` | Unauthenticated/virtual visitors (not logged in) |
| **Public** | `WellKnownUsers.Public` | Baseline permissions for all authenticated users |

When no user context is available (empty userId or virtual user), permissions are evaluated for the **Anonymous** user. Authenticated users automatically inherit **Public** permissions in addition to their own.

```csharp
// Grant Anonymous users read access to the Welcome page
await securityService.AddUserRoleAsync("Anonymous", "Viewer", "Welcome", "system", ct);

// Grant all logged-in users read access to MeshWeaver content
await securityService.AddUserRoleAsync("Public", "Viewer", "MeshWeaver", "system", ct);

// Anonymous users can read Welcome but not MeshWeaver
var anonCanRead = await securityService.HasPermissionAsync("MeshWeaver/Docs", "", Permission.Read, ct);
// anonCanRead == false

// Authenticated users inherit Public permissions
var authCanRead = await securityService.HasPermissionAsync("MeshWeaver/Docs", "Alice", Permission.Read, ct);
// authCanRead == true (Alice inherits Public's Viewer role)
```

# Hierarchical Access Pattern

```mermaid
flowchart TB
    Global["Global Scope<br/>(empty namespace)"] --> Org["Organization<br/>e.g., ACME"]
    Org --> Proj["Project<br/>e.g., ACME/ProjectX"]
    Proj --> Task["Task<br/>e.g., ACME/ProjectX/Task1"]

    style Global fill:#4caf50,color:#fff
    style Org fill:#2196f3,color:#fff
    style Proj fill:#ff9800,color:#fff
    style Task fill:#9c27b0,color:#fff
```

**Examples:**
- Global Admin: `AddUserRoleAsync("Roland", "Admin", null, ...)` → full access everywhere
- Org Editor: `AddUserRoleAsync("Alice", "Editor", "ACME", ...)` → edit within ACME and descendants
- Project Viewer: `AddUserRoleAsync("Bob", "Viewer", "ACME/ProjectX", ...)` → read-only at ProjectX

# Access Control UI

The Access Control layout area (`AccessControlArea`) provides:

1. **Inherited Permissions** (read-only markdown table): Shows AccessAssignment nodes from ancestor scopes, displaying Subject, Role, Source path, and Allow/Deny status.

2. **Local Assignments** (editable): Shows AccessAssignment nodes that are direct children of the current node. Admins can toggle Allow/Deny and delete assignments.

3. **Add Assignment** (admin-only): Dialog with autocompleting comboboxes for Subject (User/Group search via IMeshService) and Role selection.

# Partition Access Control

In multi-tenant PostgreSQL deployments, each organization has its own schema (partition). Access to partitions is controlled by the `partition_access` table:

```sql
CREATE TABLE public.partition_access (
    user_id    TEXT NOT NULL,
    partition  TEXT NOT NULL,
    PRIMARY KEY (user_id, partition)
);
```

Populated automatically by `rebuild_user_effective_permissions()` in each partition's schema. When a user has any role in a partition, they get a `partition_access` entry.

## Partition Access in Search

Cross-schema search (`search_across_schemas`) enforces partition access at the SQL level. The access control clause requires:

1. **Partition access** — user must have `partition_access` entry for the schema (always required)
2. **Node-level permission** — user must have Read permission on the node's `main_node` path

`public_read` node types (e.g., User, Markdown) skip the node-level check but still require partition access. This prevents cross-partition data leakage — a user can't see another organization's nodes just because the node type is publicly readable.

```sql
-- Access control: partition_access is ALWAYS required.
-- public_read only skips node-level permission checks.
WHERE partition_access_exists AND (
    public_read_node_type OR node_level_permission
)
```

## AI Tool Call Identity

When AI agents execute tool calls (Get, Update, Create, etc.) during thread streaming, the user's `AsyncLocal` access context doesn't flow through the AI framework's async tool invocation chain. All tools are wrapped with `AccessContextAIFunction` (a `DelegatingAIFunction`) that restores the user's identity from `ThreadExecutionContext.UserAccessContext` before each invocation.

This ensures tool calls run with the correct user identity for permission checks.

## Satellite Node Permissions

Satellite node types (Thread, Comment, ApiToken) use `GetPermissionForNodeType` to map to their required permission:

| Node Type | Required Permission |
|-----------|-------------------|
| Thread, ThreadMessage | `Permission.Thread` |
| Comment | `Permission.Comment` |
| ApiToken | `Permission.Api` |
| All others | `Permission.Create` |

# PostgreSQL Integration

For PostgreSQL deployments, a denormalized `user_effective_permissions` table enables fast query-time permission checks. A trigger on `mesh_nodes` automatically rebuilds this table when AccessAssignment or GroupMembership nodes change.

```sql
-- Trigger fires on AccessAssignment/GroupMembership changes
CREATE TRIGGER mesh_node_access_changed
    AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
    FOR EACH ROW EXECUTE FUNCTION trg_mesh_node_access_changed();
```

The rebuild function:
1. Reads AccessAssignment MeshNodes from `mesh_nodes`, unnesting each node's `roles` JSON array via `jsonb_array_elements(content->'roles')`
2. Expands GroupMembership recursively (nested groups)
3. Joins with Role definitions (built-in + custom Role MeshNodes)
4. Produces per-user, per-permission rows in a shadow table
5. Atomically swaps the shadow table into the live table

# Node Validation (INodeValidator)

The `RlsNodeValidator` integrates with the mesh node CRUD pipeline to enforce permissions on Create, Update, and Delete operations:

```csharp
public class RlsNodeValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations
        => [NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    public async Task<NodeValidationResult> ValidateAsync(
        NodeValidationContext context, CancellationToken ct)
    {
        var requiredPermission = context.Operation switch
        {
            NodeOperation.Create => Permission.Create,
            NodeOperation.Update => Permission.Update,
            NodeOperation.Delete => Permission.Delete,
            _ => Permission.None
        };

        var hasPermission = await securityService
            .HasPermissionAsync(context.Node.Path, requiredPermission, ct);

        return hasPermission
            ? NodeValidationResult.Valid()
            : NodeValidationResult.Invalid(NodeRejectionReason.Unauthorized);
    }
}
```

Read operations are not validated by the node validator — read filtering is handled by `SecurePersistenceServiceDecorator` which wraps `GetChildrenAsync` and `GetNodeAsync` with permission checks.

# Hub Identity and ImpersonateAsHub

## How Hubs Authenticate

Every message in MeshWeaver carries an `AccessContext` that identifies the sender. The `UserServicePostPipeline` automatically attaches this context to outgoing messages:

1. **User in scope** — if a user is authenticated (e.g., via Blazor circuit), their `AccessContext` is attached.
2. **ImpersonateAsHub()** — if the message was posted with `PostOptions.ImpersonateAsHub()`, the hub's own address becomes the identity.
3. **Hub-to-hub fallback** — if neither of the above applies, the hub's address is used as a fallback identity.

The identity is set **per-message on the delivery**, not globally on a service. This prevents spoofing — the hub's address comes from the hub itself and cannot be overridden by callers.

## Using ImpersonateAsHub()

When a hub needs to perform an operation as itself (not as the current user), use `ImpersonateAsHub()` on the post options:

```csharp
// Portal hub creates a VUser node as itself
var response = await portalHub.AwaitResponse(
    new CreateNodeRequest(vUserNode),
    o => o.WithTarget(meshHubAddress).ImpersonateAsHub(),
    ct);
```

The hub's address (e.g., `portal/mysite`) becomes the `AccessContext.ObjectId` on the message delivery. The receiving handler uses this identity for permission checks.

**Key properties:**

| Property | Value |
|----------|-------|
| `AccessContext.ObjectId` | Hub address as full string (e.g., `portal/mysite`) |
| `AccessContext.Name` | Hub address display name |
| Scope | Per-message (not per-service) |
| Spoofing | Not possible — address comes from the hub itself |

## Identity Resolution in Node Operations

When `HandleCreateNodeRequest` receives a message, it resolves the identity:

1. If `CreateNodeRequest.CreatedBy` is explicitly set, it is used as-is.
2. If `CreatedBy` is empty, the handler fills it from `AccessContext.ObjectId` on the message delivery.

The same pattern applies to `UpdateNodeRequest.UpdatedBy` and `DeleteNodeRequest.DeletedBy`.

## ImpersonateAsNode() on IMeshService

`IMeshService` automatically resolves identity from `AccessService.Context.ObjectId`. When `ImpersonateAsNode()` is called, it switches to the hub's own address:

```csharp
var factory = hub.ServiceProvider.GetRequiredService<IMeshService>();

// Normal: createdBy = AccessService.Context.ObjectId (current user)
await factory.CreateNodeAsync(node, ct: ct);

// Impersonated: createdBy = hub.Address, AccessContext = hub identity
var impersonated = factory.ImpersonateAsNode();
await impersonated.CreateNodeAsync(node, ct: ct);
```

Internally, `ImpersonateAsNode()` sets a flag on the same class — `createdBy`/`updatedBy`/`deletedBy` resolve to `hub.Address.ToFullString()` and `PostOptions.ImpersonateAsHub()` is added. The hub must have the required roles on the target namespace.

**When to use:**
- Background jobs or automated processes without a user session
- Hub-to-hub operations where the hub acts on its own behalf
- System-level node management (auto-generated content, cleanup tasks)

# Per-Node-Type Access Rules (INodeTypeAccessRule)

## Overview

Some node types require custom access logic that differs from the standard AccessAssignment-based RLS check. For example, VUser nodes should only be creatable by portal hubs, regardless of AccessAssignment configurations.

The `INodeTypeAccessRule` interface allows node types to replace the standard RLS check with custom logic:

```csharp
public interface INodeTypeAccessRule
{
    string NodeType { get; }
    IReadOnlyCollection<NodeOperation> SupportedOperations { get; }
    Task<bool> HasAccessAsync(
        NodeValidationContext context, string? userId, CancellationToken ct);
}
```

When `RlsNodeValidator` encounters a node whose type has a registered `INodeTypeAccessRule`, it delegates to the rule **instead of** checking AccessAssignment permissions. The rule returns `true` to allow or `false` to deny.

## How It Works

```mermaid
flowchart TD
    A[RlsNodeValidator.ValidateAsync] --> B{Custom access rule<br/>for this NodeType?}
    B -->|Yes| C[INodeTypeAccessRule.HasAccessAsync]
    B -->|No| D[Standard RLS:<br/>Check AccessAssignment permissions]
    C -->|true| E[Valid]
    C -->|false| F[Unauthorized]
    D -->|Has permission| E
    D -->|No permission| F
```

## Registering a Custom Access Rule

Register via DI in your node type's configuration method:

```csharp
public static TBuilder AddVUserType<TBuilder>(this TBuilder builder)
    where TBuilder : MeshBuilder
{
    builder.AddMeshNodes(CreateMeshNode());
    builder.ConfigureServices(services =>
    {
        services.AddSingleton<INodeTypeAccessRule, VUserAccessRule>();
        return services;
    });
    return builder;
}
```

## Example: VUser Access Rule

The VUser node type uses a custom access rule that allows portal namespace hubs to create, read, and update VUser nodes:

```csharp
private class VUserAccessRule : INodeTypeAccessRule
{
    public string NodeType => "VUser";

    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Read, NodeOperation.Update];

    public Task<bool> HasAccessAsync(
        NodeValidationContext context, string? userId, CancellationToken ct)
    {
        // Allow if the identity is in the portal namespace
        if (!string.IsNullOrEmpty(userId) &&
            userId.StartsWith("portal/", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(true);

        // Deny all others
        return Task.FromResult(false);
    }
}
```

**Key behaviors:**
- Only identities starting with `portal/` can create, read, or update VUser nodes.
- Other identities are denied — the standard AccessAssignment check is **not** performed for VUser nodes.
- Delete operations are not covered by this rule and fall through to standard RLS.

## End-to-End: Portal Hub Creating a VUser

```mermaid
sequenceDiagram
    participant Portal as Portal Hub<br/>(portal/mysite)
    participant Pipeline as UserServicePostPipeline
    participant Mesh as Mesh Hub
    participant RLS as RlsNodeValidator
    participant Rule as VUserAccessRule

    Portal->>Pipeline: Post(CreateNodeRequest, ImpersonateAsHub())
    Pipeline->>Pipeline: AccessContext already set → skip
    Pipeline->>Mesh: Deliver message
    Mesh->>RLS: ValidateAsync(VUser node, Create)
    RLS->>RLS: NodeType="VUser" → custom rule exists
    RLS->>Rule: HasAccessAsync(userId="portal/mysite")
    Rule-->>RLS: true (portal namespace)
    RLS-->>Mesh: Valid
    Mesh-->>Portal: CreateNodeResponse(Success)
```

# Message-Level Permission Enforcement

## RequiresPermissionAttribute

Message types can declare the permission they require via `[RequiresPermission]`. When a message arrives at a node hub with the `AccessControlPipeline` enabled, the pipeline checks whether the sender has the required permission on the hub's path. If denied, a `DeliveryFailure` with `ErrorType.Unauthorized` is returned.

```csharp
// Simple: single permission on the hub path
[RequiresPermission(Permission.Read)]
public record SubscribeRequest(...);

[RequiresPermission(Permission.Create)]
public record CreateNodeRequest(...);

[RequiresPermission(Permission.Update)]
public record DataChangeRequest(...);
```

### Built-in Annotated Messages

| Message | Required Permission |
|---------|-------------------|
| `SubscribeRequest` | Read |
| `GetDataRequest` | Read |
| `CreateNodeRequest` | Create |
| `ImportNodesRequest` | Create |
| `ImportContentRequest` | Create |
| `UpdateNodeRequest` | Update |
| `DataChangeRequest` | Update |
| `UndoActivityRequest` | Update |
| `RollbackNodeRequest` | Update |
| `UpdateUnifiedReferenceRequest` | Update |
| `DeleteNodeRequest` | Delete |
| `DeleteContentRequest` | Delete |
| `DeleteUnifiedReferenceRequest` | Delete |
| `MoveNodeRequest` | Custom (see below) |

### Custom Permission Checks

For messages that need non-trivial authorization logic, inherit from `RequiresPermissionAttribute` and override `GetPermissionChecks`. The method receives the `IMessageDelivery` and the hub path, and returns multiple `(path, permission)` pairs — all must pass.

```csharp
// MoveNodeRequest needs Delete on source + Create on target
[MoveNodePermission]
public record MoveNodeRequest(string SourcePath, string TargetPath);

public class MoveNodePermissionAttribute() : RequiresPermissionAttribute(Permission.Update)
{
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        if (delivery.Message is MoveNodeRequest move)
        {
            yield return (GetNamespace(move.SourcePath), Permission.Delete);
            yield return (GetNamespace(move.TargetPath), Permission.Create);
        }
        else
        {
            yield return (hubPath, Permission.Update);
        }
    }

    private static string GetNamespace(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : path;
    }
}
```

### Extending with Custom Permissions

The `Permission` enum uses `[Flags]` with bits 1–32 reserved for built-in permissions. Custom permissions use higher bits:

```csharp
const Permission Approve = (Permission)64;
const Permission Publish = (Permission)128;

// Custom message requiring Approve permission
[RequiresPermission((Permission)64)]
public record ApproveDocumentRequest(string Path);
```

## AccessControlPipeline

The `AccessControlPipeline` is a delivery pipeline step registered by `AddRowLevelSecurity()` on all default node hubs. It runs before the message handler and:

1. Reads the `RequiresPermissionAttribute` from the message type (cached per type)
2. Calls `GetPermissionChecks()` to get the list of `(path, permission)` pairs
3. Checks each pair against `ISecurityService.HasPermissionAsync()`
4. If any check fails → sends `DeliveryFailure(ErrorType.Unauthorized)` back to sender

Messages without `[RequiresPermission]` pass through unchecked. System messages (`PingRequest`, `InitializeHubRequest`, etc.) are not annotated and are always allowed.

# Configuration

Enable row-level security in your mesh configuration:

```csharp
var builder = new MeshBuilder()
    .UseMonolithMesh()
    .AddFileSystemPersistence(dataPath)
    .AddRowLevelSecurity();  // Registers ISecurityService, RlsNodeValidator, etc.
```

# Best Practices

1. **Start with hierarchy** — assign roles at the organizational level and let inheritance handle descendants
2. **Use deny sparingly** — deny overrides only the specific role, not all permissions
3. **Anonymous for unauthenticated access** — configure Anonymous user with Viewer role on namespaces that should be visible without login
3. **Public for authenticated baseline** — configure Public user with Viewer role on namespaces that all logged-in users should access
4. **Cache permissions** — SecurityService caches effective permissions with a 5-minute sliding expiration
5. **Fail closed** — no roles assigned means no permissions (Permission.None)
6. **Audit via MeshNodes** — AccessAssignment nodes provide a clear audit trail of who has access to what
7. **Use ImpersonateAsHub() for hub operations** — when a hub needs to perform operations as itself, use `PostOptions.ImpersonateAsHub()` instead of setting identity on `AccessService` directly
8. **Custom access rules for special node types** — use `INodeTypeAccessRule` when a node type needs access logic that differs from standard AccessAssignment-based RLS (e.g., namespace-based identity checks)
