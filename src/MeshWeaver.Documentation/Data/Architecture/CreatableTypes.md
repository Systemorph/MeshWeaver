---
Name: Creatable Types
Category: Architecture
Description: What may be created under a node — the one resolution rule the Create form asks, what a parent NodeType can restrict, what it can extend, and what the default is.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 5v14"/><path d="M5 12h14"/></svg>
---

# Creatable Types

**What may be created under a node has exactly one answer, and it is
`ICreatableTypesProvider.GetCreatableTypes(nodePath, parentNode)`.** The Create form
(`CreateLayoutArea`) asks it and renders the result; nothing else computes a second answer.

```csharp
// MeshWeaver.Graph/CreateLayoutArea.cs — the type picker's only source
var creatableTypes = host.Hub.ServiceProvider
    .GetRequiredService<ICreatableTypesProvider>()
    .GetCreatableTypes(parentPath, currentNode);
```

The picker therefore carries **items and no queries**. That is not a style choice: a
`MeshNodePickerControl.Queries` leg runs on the client and merges its rows into the same candidate
set, so a query alongside the provider would re-admit by discovery exactly what a restricting
parent excluded.

🚨 **The picker is not the enforcement point — the form's `type` VALUE is.** The Create button reads
`form["type"]`, never the picker's item list, and that value is seeded before the provider has
answered (it cannot be otherwise — the answer is reactive). So when the resolved offer does not
contain the seed, the form replaces it with the first offered type, or with nothing when the parent
offers none, so a Required field blocks the submit. Without that, a parent declaring
`CreatableTypes` with `IncludeGlobalTypes: false` would render a picker holding only its declared
types and still create `Markdown` for anyone who submitted without touching the field — the menu
honouring the declaration and the write ignoring it.

## The resolution rule

Four sources are merged, deduplicated by NodeType path, and ordered by `Order` then name.

| # | Source | Governed by |
|---|---|---|
| 1 | **Ancestor-scoped discovery** — `nodeType:NodeType scope:selfAndAncestors namespace:{nodePath} context:create` | the mesh |
| 2 | **Child types of the parent's own type** — `namespace:{parentNode.NodeType} nodeType:NodeType context:create`, so an `ACME/Project` instance offers `ACME/Project/Todo` | the mesh |
| 3 | **The parent type's `CreatableTypes`** — `NodeTypeDefinition.CreatableTypes` on the NodeType of `parentNode` | the type author |
| 4 | **The global set** — `MeshConfiguration.GlobalCreatableTypes` (`Markdown`, `Thread`, `Agent`, `NodeType` by default) | the host, opt-out per type |

Plus every **static registration** the host contributed through `AddMeshNodes` /
`IStaticNodeProvider` that has not opted out of `context:create`. At the ROOT path (no parent) there
is no namespace to scope to, so source 1 becomes the declared mesh-wide NodeType catalog
(`MeshWideQuery`) — a catalog is mesh-wide by nature and says so (see
[Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination)).

### What a parent can restrict

**`CreatableTypes` is a WHITELIST, not merely an addition.** When the parent node's NodeType
declares one, sources 1, 2 and the static bucket are filtered down to it. A type the
ancestor-scoped query found and the parent did not declare is withheld.

```json
{ "creatableTypes": ["Crm/Question"] }
```

Under an instance of that type, `Crm/Question` is the only discovered type offered — the global set
still rides along (below).

### What a parent can extend

The same list ADDS. A declared path that no query returned is offered anyway, resolved from the
static registry when it is there and synthesised from the path when it is not. **This is the half
that no namespace-scoped query can reach**: in `Systemorph/MeshWeaver.Crm`, `Crm/Offer` declares
`Crm/Question` while the instances live in a different partition (`PearlTechnology/Commercials`), so
`Crm/Question` is in no ancestor chain of the node being created under. The declaration is the only
thing that puts it in the menu.

### What the default is

**A parent that declares nothing restricts nothing.** With `CreatableTypes` absent, every discovered
type and every static registration is offered — exactly the set the Create form offered before it
asked the provider. `CreateMenuHonoursTheParentTypeTest.AParentDeclaringNothingLosesNothing` pins
that as a SUPERSET assertion against the two query literals the form used to run, because the
failure it guards against is invisible: a narrower source would shrink every Create menu in the
fleet with no error and no empty state.

### `IncludeGlobalTypes`

`NodeTypeDefinition.IncludeGlobalTypes` defaults to `true` and rides along with a whitelist — a type
that restricts discovery to `["Crm/Question"]` still offers Markdown, Thread, Agent and NodeType.
Set it to `false` to seal that off. The property carries
`[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` on purpose: the initializer defaults to `true`,
so an explicit `false` equals `default(bool)` and the hub's `WhenWritingDefault` policy would
otherwise omit it and silently round-trip the opt-out back to `true`.

### Opting a type out of creation entirely

`ExcludeFromContext: ["create"]` on the NodeType is how a type says it is not creatable —
`Release`, `Build`, `ModuleBuild` and `Partition` all use it. Every query above names
`context:create`, and the static bucket applies the same filter, so the opt-out is honoured on both
legs.

🚨 **A type's own opt-out beats a list that names it.** Sources 3 and 4 resolve through the same
exclusion-aware lookup, so a parent's `CreatableTypes` — or a host's `GlobalCreatableTypes` —
naming `Partition` does not resurrect it. A whitelist ADDS types the queries could not reach; it
does not overrule a type's own statement that instances of it are made by the platform rather than
by a person. See [Query Syntax](/Doc/DataMesh/QuerySyntax) for the `context:` qualifier and the other contexts.

## Two rules that look like details and are not

🚨 **The static bucket is filtered by the create-context opt-out, NEVER by `NodeType == "NodeType"`.**
A built-in type registration is `AddMeshNodes(new MeshNode("Group") { HubConfiguration = … })` — the
PATH is the type name and there is no self-typing stamp at all. Measured on a running mesh (#4040),
filtering the static bucket on that stamp kept **7 of 42** registrations and silently dropped
`Markdown`, `Group`, `Role`, `Redirect`, `UiContribution`, `HomeTab`, `License` and `WhatsNew` —
every one a type the Create form has always offered.

🚨 **There is no root-namespace query leg, and adding one back would achieve nothing.**
`namespace:` with an empty value leaves `ParsedQuery.Path` empty, which is precisely the shape the
Postgres planner refuses as unanchored (see
[Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination)) — so on a partitioned portal that
leg has never returned a row, whatever it looked like it was doing. Root-level built-ins reach the
menu through the static bucket, which is where they actually live.

## Where the code is

| Concern | File |
|---|---|
| The contract | `src/MeshWeaver.Mesh.Contract/Services/ICreatableTypesProvider.cs` |
| The resolution | `src/MeshWeaver.Graph/Configuration/CreatableTypesProvider.cs` |
| The declaration | `src/MeshWeaver.Graph.Contract/NodeTypeDefinition.cs` (`CreatableTypes`, `IncludeGlobalTypes`) |
| The global set | `src/MeshWeaver.Mesh.Contract/MeshConfiguration.cs` (`GlobalCreatableTypes`) |
| The form | `src/MeshWeaver.Graph/CreateLayoutArea.cs` |
| The tests | `test/MeshWeaver.Graph.Test/CreateMenuHonoursTheParentTypeTest.cs` |

Related: [Adding a New Node Type](/Doc/Architecture/AddingANewNodeType) · [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess)
