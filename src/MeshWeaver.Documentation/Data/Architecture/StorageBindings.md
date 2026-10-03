---
Name: Storage Bindings — Where a Partition Keeps Its Data
Category: Architecture
Description: With no configuration, everything is stored where the instance would put it anyway. A storage binding overrides one purpose of one partition — doc parts, original files, the vector index, durable stream state — by picking an existing container in one of the instance's pre-configured stores or creating a new one there. The node shape, where bindings live, the resolver every storage consumer asks, validation, access, and the settings surfaces.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/></svg>
---

# Storage Bindings — Where a Partition Keeps Its Data

**Nobody has to configure anything.** With zero bindings, every purpose resolves to what the
instance does today: its own PostgreSQL, in the partition's own schema; its own content storage.
A **storage binding** only *overrides* — one purpose, of one partition — and only once it has been
validated.

The default flow is **select-or-create inside the pre-configured storage**: the instance discovers
its own stores from its configuration and identity, lists the containers that exist in them, and a
binding either *picks* one of those or *creates* a new one there with the instance's own identity.
A completely separate account is the advanced option and is recorded as a vault reference only.

## Purposes

`StoragePurpose` is an open vocabulary of string constants (policy
`open-vocabulary-string-constants`), spelled as an enum would be:

| Purpose | What it stores | Built-in default |
|---|---|---|
| `DurableStream` | durable stream state (queues, checkpoints) | the instance database — **instance-wide**, so a partition without its own binding falls back to `Admin`'s |
| `DocParts` | the parts a document is cut into | the instance database, the partition's schema |
| `Originals` | the original files of a content collection | the instance's content storage |
| `VectorIndex` | the vector index (`content_chunks`) | the instance database, the partition's schema |

A module may bind a purpose of its own in the same field; an unknown purpose simply has no binding
and resolves to the default.

## Where a binding lives

**A binding lives in the partition whose storage it configures**, in that partition's own
`storage` table:

| Scope | Path | Who administers it |
|---|---|---|
| a partition (a Space, a person, a package) | `{partition}/_Storage/{id}` | whoever holds **Update** on the partition root, by ordinary grants |
| the instance (global) | `Admin/_Storage/{id}` | a platform admin (Update on `Admin`) |

`_Storage` is a satellite segment (`SatelliteTableMapping.Defaults` → table `storage`, node type
`StorageBinding`), placed **directly under the partition root** — never nested under another
satellite. The `storage` table is created by `public.ensure_partition_schema` like every other
satellite table: a new partition gets it at provisioning, an existing one on the next migration
pass or the first touch of a fresh pod.

## The node

`StorageBinding` (`MeshWeaver.Mesh.Storage`):

| Field | Meaning |
|---|---|
| `Purpose` | what it is for |
| `StoreId` | the pre-configured store; empty = the store the instance uses by default for the purpose |
| `Container` | the container in it — a Postgres schema, a blob container, a directory |
| `TablePrefix` | a prefix for the tables the purpose creates, where the store has tables |
| `Collection` | the content collection it serves; empty = the whole partition |
| `IsDefault` | the partition's default binding for the purpose |
| `NewContainerName` | input to **Create** |
| `VaultSecretName` / `ManagedIdentityClientId` / `ExternalEndpoint` | ADVANCED — a separate account, as a vault reference. **Never a secret.** |
| `RequestedAction`, `RequestedAt` | what the viewer asked the binding's hub to do (`Validate`, `Create`) |
| `ValidationStatus`, `ValidationMessage`, `ValidatedAt`, `ValidatedTarget` | the verdict, recorded on the node by its own hub |

A sample binding, in the partition `fabrikam`:

```json
{
  "id": "parts",
  "namespace": "fabrikam/_Storage",
  "nodeType": "StorageBinding",
  "content": {
    "$type": "StorageBinding",
    "purpose": "DocParts",
    "storeId": "postgres",
    "container": "fabrikam_parts",
    "isDefault": true,
    "validationStatus": "Valid",
    "validationMessage": "'fabrikam_parts' exists in 'Instance database (PostgreSQL · <database>)' and is reachable."
  }
}
```

## Pre-configured stores

An `IInstanceStore` is one of the instance's own stores, registered in DI by the module that owns
the connection. It lists its containers live, creates one idempotently with the instance's
identity, and probes one. Every call goes through `IIoPool`.

| Store | Kind | Registered by | Default for |
|---|---|---|---|
| `postgres` — the mesh's database | `PostgresSchema` | `MeshWeaver.Hosting.PostgreSql` (partitioned persistence) | `DocParts`, `VectorIndex`, `DurableStream` |
| `content` — the content storage root, `{Storage:BasePath}/content` | `Directory` | `Memex.Portal.Shared` when `Storage:SourceType` is `FileSystem` | `Originals` |
| `blob` — the content-storage blob account | `BlobContainer` | `Memex.Portal.Distributed` on the Azure backend | `Originals` when `Storage:SourceType` is `AzureBlob` |

A store's `DefaultContainerFor(partition)` is exactly what the instance uses today (Postgres:
`lower(partition)`, the partition's schema).

### Which containers a partition may bind

The stores are **shared**: every partition's schema sits in the same database. A partition that
could bind any existing schema could bind *another partition's* — and read or overwrite its data.
So (`StorageContainerOwnership`):

- a partition may bind its default container in the store, or a container **named for it** — its
  prefix alone or followed by `_` or `-` and more (`fabrikam`, `fabrikam_parts`, `fabrikam-originals`);
- a name that is **another partition's default container** is refused even when it matches the
  prefix (a partition `fabrikam_parts` next to `fabrikam`) — the validation reads the partition
  catalog;
- `Admin` — written only by platform admins — may bind any container.

The resolver re-applies the prefix rule before it honours a binding, so a verdict written onto a
node by hand cannot widen what a partition may bind.

## Validation — on every save

The binding's **own hub** runs a watcher (`StorageBindingValidation`). Whenever the target changes
(store, container, prefix, separate account — every field `TargetKey()` covers) or a
`RequestedAction` arrives, it checks, in order: separate account (recorded as `Unverified` — this
instance has no connector for separate accounts, so such a binding never overrides), the store
exists, the name passes the store's own rule, the partition may use it, it is not another
partition's default — and only then contacts the store (`Probe`, or `EnsureContainer` for
**Create**). The verdict is written onto the node as System, about the target it was decided on;
an edit that landed meanwhile is validated in its own turn. The trigger is a pure function of the
content that is `null` once the verdict for the current target is recorded, so the watcher never
feeds on its own writes.

| Status | Meaning |
|---|---|
| `Pending` | not validated yet, or the target moved |
| `Valid` | reachable, the container exists, the instance's identity may write to it — **the only status that overrides** |
| `Invalid` | a malformed name, a container the partition may not use, a store that does not exist, a container that does not exist |
| `Unreachable` | the store did not answer, or refused the instance's identity |
| `Unverified` | a separate account — recorded, never used |

## The resolver — what consumers call

```csharp
public interface IStorageBindingResolver
{
    IObservable<ResolvedStorage> Resolve(string purpose, string partition, string? collection = null);
}

public sealed record ResolvedStorage(
    string Purpose, string Partition, string StoreId, string StoreKind, string Container)
{
    public string? TablePrefix { get; init; }
    public string? BindingPath { get; init; }   // null ⇒ the built-in default
    public bool IsDefault => BindingPath is null;
    public string? VaultReference { get; init; }
}
```

The answer is **live** — emitted at once and again whenever a deciding binding changes,
de-duplicated; take the first value when you need it once. Order: the partition's own **usable**
binding (one naming `collection` before its default one) → for an instance-wide purpose,
`Admin`'s → the built-in default. `IsDefault` means *do exactly what you did before bindings
existed*; only a non-default answer needs rewiring. The resolver reads the binding nodes as the
infrastructure identity — storage routing never depends on who triggers it.

## The settings surfaces

ONE content builder, two surfaces, both framework controls bound to the binding nodes:

- **Instance settings** — `/Admin/Settings/Storage` (Operations section), platform admins only:
  the global bindings at `Admin/_Storage`.
- **Partition settings** — `/{partition}/Settings/Storage` on the partition root, for whoever holds
  Update there. A platform admin gets nothing here by that role: a global admin has no data access.

Each section is a template of framework controls: the pre-configured stores as a `DataGrid` (store,
kind, what it is the default for, this partition's default container, how many containers this
partition may bind — read live), an **Add binding** button, and the partition's bindings as a
`MeshSearch` list the viewer's client runs under the viewer's own access. Each binding opens its
OWN page (`StorageBindingLayoutArea`, the node's default and Edit area): the recorded verdict as
read-only fields of the node, a `MeshNodeContentEditorControl` bound **directly to the node** —
purpose, store and container are dropdowns over the instance's stores and the containers this
partition may bind in them, read live, so a container created a moment ago is offered at once —
and **Create container** / **Validate** buttons that write `RequestedAction` on the node. The node's
hub acts and records the verdict; the page re-renders from the node. No `/data` replica, no save
loop; deleting a binding is the node's own Delete.

## What is not here yet

- **Separate accounts** are recorded (vault secret name or managed-identity client id, never a
  secret) but not connected: no store connector reads a vault reference yet, so such a binding is
  `Unverified` and never overrides.
- **The consumers** — doc-part tables, originals, the vector index, durable streams — adopt the
  resolver in their own changes; until one does, its purpose is stored at its default whatever is
  bound.
- A binding is validated when its own hub runs — on an edit, on a requested action, or when the
  settings page opens it. The resolver reads the recorded verdict and does not activate binding
  hubs itself.

See also: [Settings by Owner](../AdminApp) · [PostgreSQL schema architecture](../PostgresSchemaArchitecture) · [Satellite node patterns](../SatelliteNodePatterns).
