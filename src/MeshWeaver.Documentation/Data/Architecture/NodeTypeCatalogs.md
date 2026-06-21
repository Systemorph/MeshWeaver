---
Name: NodeType Catalogs (shipping instances of a NodeType)
Category: Architecture
Description: How a partition that ships INSTANCES of a NodeType (Agent, Harness, Skill, Model) is rooted — the partition root is a single nodeType:NodeType node that links to the registered static C# definition, so the in-memory definition is dissociated from runtime serving and never collides with the partition root.
Icon: Box
---

# NodeType Catalogs

A **NodeType catalog** is a partition that ships **instances of one NodeType** with the build: `Agent` (the agent catalog), `Harness`, `Skill`, `Model`. The instances are authored content materialized into the partition by the [Static-Repo Import](/Doc/Architecture/StaticRepoImport) and **served from the database** at runtime — like every other partition.

This page defines how such a catalog is **rooted**. Get it wrong and the partition's bare address (`@Harness`) is claimed by *two* nodes; a `GetDataRequest` to it can never settle on one owner, the mesh **routing-loop guard** fires, the partition's data source (`ds/Harness`) faults, and every subscriber — the live picker binding plus the NodeType compile/sources/release watchers — dies with it. Symptom: *the harness selector disappears until refresh*.

## The rule

> **A NodeType catalog's partition root is a single persisted `nodeType:NodeType` node whose `id` is the type name. It IS the routable partition root AND the NodeType definition, and it LINKS to the registered static C# node-type for its `HubConfiguration`. Postgres owns this node — it is the sole runtime owner of the bare partition path. The in-memory static definition is NOT a served runtime node; it serves only as (a) the definition the root links to for `HubConfiguration`, and (b) the sync source that materializes the root.**

So for the `Harness` catalog there is **exactly one** node at path `Harness`:

```
@Harness   id=Harness  nodeType=NodeType   (PG-served partition root + the "Harness" NodeType definition)
             └─ links to → the registered static C# node-type "Harness" (supplies HubConfiguration)
@Harness/MeshWeaver   nodeType=Harness     (an instance — a child of the catalog)
@Harness/_Policy      nodeType=PartitionAccessPolicy   (publicRead)
```

`HubConfiguration` is a C# delegate (`Func<MessageHubConfiguration, MessageHubConfiguration>`) — it cannot be persisted. So a **built-in** NodeType node resolves its `HubConfiguration` from the registered static type by name; a **dynamic** NodeType node compiles it from its `Source`/`HubConfigurationSource` (see [Static-Repo Import](/Doc/Architecture/StaticRepoImport)). Both are `nodeType:NodeType` nodes; they differ only in where the delegate comes from.

## The anti-pattern this replaces (why the loop happens)

Historically a NodeType catalog registered **two** nodes that landed on the **same path**:

1. The in-memory **NodeType type-definition** — `AddMeshNodes(CreateMeshNode())`, registered at path = the **NodeType discriminator** (`new("Harness")`), carrying the `HubConfiguration` delegate.
2. The DB-imported **partition root** — `IStaticRepoSource.PartitionRoot`, a `nodeType:Space` node at path = the **RootNamespace**.

When `NodeType == RootNamespace == the partition name` (true for `Harness`, `Agent`, `Skill`), **both nodes occupy the bare partition path**. Once the DB root wins the address resolution, the runtime disagrees with itself: routing serves the DB node, but `MeshDataSource.WithMeshNodes`/`FindStaticNode`/`NodeTypeEnrichmentHelpers` still find the in-memory type-def at the same path. A `GetDataRequest` for the bare partition bounces between hubs, re-enters one already in its `RoutingPath`, and the **routing-loop guard** (`MessageService`) fails it → `ds/<Partition>` faults.

`Model` only escaped this by accident: `LanguageModelNodeType.NodeType = "LanguageModel"` ≠ `RootNamespace = "Model"`, so its type-def (`@LanguageModel`) and root (`@Model`) never shared a path. **That exception is the proof** — the collision, not anything `Harness`-specific, is the defect. The unified rule above removes the collision for *every* catalog deterministically: there is only ever one node on the partition path.

## The principle: in-memory is dissociated from runtime

For a **DB-synced** partition, an in-memory static node definition is a **sync / definition root only — never a served runtime node**:

- It is **not** returned by runtime node resolution for the bare partition path (`FindStaticNode` / `TryResolveStaticNode` / `MeshDataSource.WithMeshNodes` must not serve it as the node). Postgres is the sole runtime owner.
- It **is** still consulted as a *definition* — the persisted `nodeType:NodeType` root links to it to obtain the C# `HubConfiguration` for enriching the catalog's instances.
- It **is** still the *sync source* — the importer writes the persisted root + instances from it once per content-version.

This is the same `dbSynced` boundary that already drops the in-memory **content/storage** providers (`AddHarnessType`/`AddAgentType` skip `IStaticNodeProvider` + `StaticNodePartitionStorageProvider` when the partition is synced); the rule extends that boundary to the **type-def node** so it stops squatting on the DB partition's path.

## Collecting across namespaces (the registry)

A catalog's **effective set at runtime is never one partition** — it is collected from a **collection of namespaces**: the user's own, the active space, and the platform tier. The default tiers, in precedence order:

| Tier | Namespace | Who writes it |
|---|---|---|
| User | `{user}/{Type}` | the user (their own additions/overrides) |
| Space | `{space}/{Type}` | space members (shared within a space) |
| Platform | `{Type}` | platform admins (the shipped defaults) |

One registry query resolves the union — exact-membership, per-user RLS, no graph walk:

```
namespace:{user}/{Type} | {space}/{Type} | {Type}   nodeType:{Type}
```

`AgentPickerProjection.BuildRegistryQuery` (run via `hub.GetQuery`) is the reference implementation; **Model / Provider / Harness / Skill unify onto the same shape.** The platform tier is `publicRead` (everyone sees the defaults); the user/space tiers are owner-scoped by RLS. (The `{Type}` platform schema must be searchable for the unscoped fan-out to find it — see [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture).)

## Managing the platform tier (platform-admin settings + API)

The **platform tier** (`{Type}` namespace — platform-wide keys, models, providers, agents, harnesses, skills) is governed content. It is managed by **platform admins** (`hub.IsGlobalAdmin()`; the `Admin` partition — see [Access Control](/Doc/Architecture/AccessControl)), through two surfaces that every catalog shares:

- **Settings GUIs** — node-bound editor tabs built from the framework's standard data-binding (the `Edit` macro / node-content editor controls), gated to platform admins. Never hand-rolled selects/forms/save-loops.
- **Management APIs** — the existing platform-config surface. Platform-tier mutations go through it (and `GetMeshNodeStream(path).Update(...)`), not bespoke request/response handlers.

Writes to the platform tier require platform admin; user/space tiers are owner-scoped. Secrets (provider API keys) are never stored in plain node content — they reference the secret store.

## How to ship a NodeType catalog

1. Author the instances and register the catalog as an `IStaticRepoSource` (see [Static-Repo Import](/Doc/Architecture/StaticRepoImport)). The source's `PartitionRoot` is a **`nodeType:NodeType`** node (id = the type name) that names the registered static type it links to — not a `nodeType:Space` node.
2. Register the type's `HubConfiguration` as a **definition** keyed by type name (the static registry), not as a served node at the partition path.
3. Gate it into `Features:StaticRepoSync:Partitions` so Postgres serves it.
4. The catalog's instances carry `nodeType = <TypeName>` (unchanged); enrichment resolves their `HubConfiguration` through the `@<TypeName>` NodeType root → its static link.

Adding a new such catalog is then collision-free by construction — there is one node on the partition path, owned by the database.

## Status / migration

`Model`/`LanguageModel` already avoids the collision via a distinct discriminator. `Harness`, `Agent`, `Skill` are being migrated from the `Space`-root-plus-served-type-def shape to the unified `nodeType:NodeType` root above; the migration only rewrites the partition **root** (`nodeType:Space` → `nodeType:NodeType`) — instance nodes are unchanged.

## See also

- [Static-Repo Import](/Doc/Architecture/StaticRepoImport) — how catalog content is materialized into a partition.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — per-partition schemas + which schemas are searchable.
- [MeshNodeStreamCache](/Doc/Architecture/MeshNodeStreamCache) — the per-path handle the live picker binding subscribes to.
