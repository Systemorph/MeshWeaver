---
Name: Addressability of Objects
Category: Documentation
Description: A mesh is one addressable namespace — data, schema, model, views, files, agents and access all reach by a path. This is what lets an agent go from a plain-English ask to a precise, permission-checked action.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="10" r="3"/><path d="M12 21.7C17.3 17 20 13 20 10a8 8 0 1 0-16 0c0 3 2.7 7 8 11.7z"/></svg>
---

Everything in MeshWeaver is a **node in a mesh** — a business record, its schema, the view that renders it, the spreadsheet attached to it, the agent that reasons over it, even this documentation page. A *mesh* is not a folder tree or a database; it is a single **addressable namespace** in which every one of those objects is reachable by a path. That one property — **addressability** — is what makes the platform composable by humans, by the GUI, and by AI agents alike.

This page follows a single request through the mesh — *"open the Q3 pricing page and bump the APAC discount"* — and shows how each step is really the same move: **name the object, then act on it.** Every step is written as *what the user wants* → *what the mesh does* → *what gets addressed*.

<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif">
  <defs>
    <marker id="amark" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".5"/></marker>
  </defs>
  <rect x="0" y="0" width="760" height="320" rx="12" fill="currentColor" fill-opacity=".04"/>
  <text x="380" y="28" text-anchor="middle" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".7">One node, many addressable facets</text>
  <circle cx="380" cy="165" r="52" fill="#1e88e5"/>
  <text x="380" y="162" text-anchor="middle" font-size="13" font-weight="bold" fill="#fff">@Node</text>
  <text x="380" y="180" text-anchor="middle" font-size="10" fill="#fff" fill-opacity=".85">a MeshNode</text>
  <g font-size="11" fill="#fff">
    <rect x="70" y="70" width="150" height="38" rx="9" fill="#43a047"/><text x="145" y="88" text-anchor="middle" font-weight="bold">data/</text><text x="145" y="101" text-anchor="middle" font-size="9" fill-opacity=".85">the record, as JSON</text>
    <rect x="70" y="140" width="150" height="38" rx="9" fill="#5c6bc0"/><text x="145" y="158" text-anchor="middle" font-weight="bold">schema/</text><text x="145" y="171" text-anchor="middle" font-size="9" fill-opacity=".85">the shape to edit it</text>
    <rect x="70" y="210" width="150" height="38" rx="9" fill="#8e24aa"/><text x="145" y="228" text-anchor="middle" font-weight="bold">model/</text><text x="145" y="241" text-anchor="middle" font-size="9" fill-opacity=".85">the whole data model</text>
    <rect x="540" y="70" width="150" height="38" rx="9" fill="#f57c00"/><text x="615" y="88" text-anchor="middle" font-weight="bold">area/</text><text x="615" y="101" text-anchor="middle" font-size="9" fill-opacity=".85">a rendered view</text>
    <rect x="540" y="140" width="150" height="38" rx="9" fill="#e53935"/><text x="615" y="158" text-anchor="middle" font-weight="bold">content/</text><text x="615" y="171" text-anchor="middle" font-size="9" fill-opacity=".85">attached files</text>
    <rect x="540" y="210" width="150" height="38" rx="9" fill="#26a69a"/><text x="615" y="228" text-anchor="middle" font-weight="bold">_Access · _Thread</text><text x="615" y="241" text-anchor="middle" font-size="9" fill-opacity=".85">who may · the chat</text>
  </g>
  <line x1="220" y1="89" x2="332" y2="150" stroke="currentColor" stroke-opacity=".4" stroke-width="1.4" marker-end="url(#amark)"/>
  <line x1="220" y1="159" x2="326" y2="165" stroke="currentColor" stroke-opacity=".4" stroke-width="1.4" marker-end="url(#amark)"/>
  <line x1="220" y1="229" x2="332" y2="182" stroke="currentColor" stroke-opacity=".4" stroke-width="1.4" marker-end="url(#amark)"/>
  <line x1="540" y1="89" x2="428" y2="150" stroke="currentColor" stroke-opacity=".4" stroke-width="1.4" marker-end="url(#amark)"/>
  <line x1="540" y1="159" x2="434" y2="165" stroke="currentColor" stroke-opacity=".4" stroke-width="1.4" marker-end="url(#amark)"/>
  <line x1="540" y1="229" x2="428" y2="182" stroke="currentColor" stroke-opacity=".4" stroke-width="1.4" marker-end="url(#amark)"/>
  <text x="380" y="300" text-anchor="middle" font-size="11" fill="currentColor" fill-opacity=".55">The prefix after the path selects a facet — one address, resolved to whichever aspect you asked for.</text>
</svg>

*One node exposes many facets. A prefix after the path — `data/`, `schema/`, `model/`, `area/`, `content/` — selects which one you address.*

---

# What a mesh is

A **mesh** is the running set of message hubs that together hold all your content, plus the single namespace that addresses it. Three ideas are enough to work in it:

| Idea | Meaning |
|---|---|
| **Node** | The unit of content. A pricing model, a document, a chart, a user, an agent — each is a `MeshNode` with a `NodeType`, a `Content` payload, and a **path**. |
| **Path** | The node's address in the mesh — `Systemorph/Pricing/Q3`. Human-readable, hierarchical, resolved by best-match scoring, never a random id. |
| **Partition** | A slice of the mesh with its own owner, storage schema and access rules — a Space, a user's home, the `Doc` partition you are reading now. |

Because every node lives in the **same** namespace, there is no "the database vs. the file store vs. the UI state vs. the docs." There is one place, and everything in it has an address. The mesh *is* the workspace — you do not download it to a local tree and reconcile; you address it in place. See [Spaces](/Doc/DataMesh/Spaces) and [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) for how partitions map to storage.

The addressing syntax itself — the `@` / `@@` notation, relative vs. absolute paths, the prefix table — is the [Unified Path](/Doc/DataMesh/UnifiedPath). This page is about *why* that single addressing scheme is the backbone of the whole platform.

---

# 1 · Know where you are

> **The user wants:** *"Open the Q3 pricing page."*
> **The operation:** navigate to the node — `navigate_to @Systemorph/Pricing/Q3`.
> **What's addressed:** the **current location**.

Navigation sets a single fact that everything downstream depends on: *the path of the node you are looking at.* The GUI knows it because the route is `@{address}/{area}`; an agent knows it because its context is a node path. From that anchor, every relative reference (`../Sibling`, `data/`, `schema/`) resolves without the user ever spelling out a full path. Addressability starts with knowing your own address.

---

# 2 · Get the data behind the page

> **The user wants:** *"What are the numbers on this page?"*
> **The operation:** read the node's data — `get @Systemorph/Pricing/Q3/data/`.
> **What's addressed:** the node's **`Content`, as structured data**.

The `data/` prefix returns the node's payload as JSON — or a single entity with `data/Products/p-42`, or every collection on the node with a bare `data/`. It is fetched **live** from the owning hub, so it always reflects current state; it is never a stale copy. In a document you embed the same thing with `@@…/data/` and get a live grid. See [Data Prefix](/Doc/DataMesh/UnifiedPath/DataPrefix) and [CQRS & Content Access](/Doc/Architecture/CqrsAndContentAccess) for read semantics.

---

# 3 · Get the schema before you change it

> **The user wants:** *"Bump the APAC discount to 12%."*
> **The operation:** read the shape first — `get @Systemorph/Pricing/Q3/schema/`.
> **What's addressed:** the node's **`ContentType` schema**.

To modify safely, you first need to know *what is modifiable* — which fields exist, their types, which are required, what the enums allow. The `schema/` prefix returns exactly that: the authoritative JSON Schema of the node's content type, pulled live from the type registry. An agent asked to edit reads the schema, constructs a valid patch, and writes it back with `patch` / `update` — the write routes to the owning hub through `GetMeshNodeStream(path).Update(...)`. Schema-first editing is why the mesh can accept "change the discount to 12%" and turn it into a correct, typed mutation. See [Schema Prefix](/Doc/DataMesh/UnifiedPath/SchemaPrefix).

---

# 4 · One definition, three projections — C#, schema, and the database

Point 3 works because the schema is not written by hand and kept in sync — it is **generated from the same C# type that defines the storage**. A node's content type is one definition with three faces:

**The C# record — the single source of truth. XML doc comments are not decoration; they become the field descriptions in the schema and the agent's understanding of each field.**

```csharp
/// <summary>A regional discount applied to a pricing model.</summary>
public record RegionalDiscount
{
    /// <summary>The sales region this discount applies to. References a Region dimension node.</summary>
    [Dimension<Region>]
    public string Region { get; init; }

    /// <summary>Discount as a fraction of list price, 0.0–1.0. E.g. 0.12 = 12%.</summary>
    public double Rate { get; init; }

    /// <summary>When the discount takes effect.</summary>
    public DateOnly EffectiveFrom { get; init; }
}
```

**The JSON Schema — projected from the type at `@@…/schema/RegionalDiscount`.** The `<summary>` text lands in `description`, the CLR types become JSON types, `[Dimension]` marks `region` as a reference to be resolved by search (step 5):

```json
{
  "type": "object",
  "properties": {
    "region":        { "type": "string", "description": "The sales region this discount applies to. References a Region dimension node." },
    "rate":          { "type": "number", "description": "Discount as a fraction of list price, 0.0–1.0. E.g. 0.12 = 12%." },
    "effectiveFrom": { "type": "string", "format": "date", "description": "When the discount takes effect." }
  },
  "required": ["region", "rate", "effectiveFrom"]
}
```

**The database schema — the same record, persisted.** Content is stored per partition (never in `public`); the record round-trips as JSON in the partition's node table and its properties are what the query layer filters and sorts on:

| C# property | JSON field | Storage |
|---|---|---|
| `Region` | `region` | node `Content` (JSON), indexed for `region:` filters |
| `Rate` | `rate` | node `Content` (JSON), comparable for `rate:>0.1` |
| `EffectiveFrom` | `effectiveFrom` | node `Content` (JSON), sortable |

Change the C# record and the schema *and* the persisted shape move together — there is no drift because there is only one definition. This is the chain the "modify" flow relies on: the agent reads a schema that is guaranteed to match what the database will accept. See [Creating Node Types](/Doc/DataMesh/CreatingNodeTypes) and [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture).

---

# 5 · Reference a dimension → get its allowed values

> **The user wants:** *"…the **APAC** discount."*
> **The operation:** resolve the dimension by searching — `search region APAC`.
> **What's addressed:** the **set of valid values** for a referenced dimension.

The `Region` field above is a *dimension* — it references other nodes rather than holding a free string. You do not guess its value; you **address the value space and search it.** Type `@` in an editor and autocomplete lists the candidates; an agent runs `search nodeType:Region APAC` and gets the matching node back. The value stored is the referenced node's path, so the reference stays valid and navigable. Dimensions turn "APAC" from a fragile literal into a resolved, addressable reference. See [Query Syntax](/Doc/DataMesh/QuerySyntax).

---

# 6 · Results are ordered by meaning, not the alphabet

> **The user wants:** *"Find the reinsurance treaty for Acme."*
> **The operation:** `search reinsurance treaty acme` — free text routes to semantic search.
> **What's addressed:** the mesh's **ranked** answer to a question.

When a search reference resolves, the order of the results is a business decision, not an accident of storage. `MeshQuery` fans a query out to every provider, each returns **scores**, and the aggregator sorts by relevance — a name-prefix hit outranks a substring hit, and when the query carries free-text tokens it routes through the **vector index** so the *most semantically relevant* node comes first. Alphabetical or insertion order is only ever the final tiebreaker; explicit user intent (`sort:lastModified-desc`) wins over everything. The first result being the *right* result is what lets an agent act on "the Acme treaty" without a disambiguation round-trip. See [Query Result Scoring](/Doc/Architecture/QueryResultScoring) and [Vector Search](/Doc/Architecture/VectorSearch).

---

# 7 · Address any content object — including the spreadsheet

> **The user wants:** *"Can you double-check the numbers in billing.xlsx?"*
> **The operation:** address the file — `get @Systemorph/Pricing/Q3/content/billing.xlsx`.
> **What's addressed:** a **file in the node's content collection**.

Files are first-class addressable objects, not opaque attachments. Every node can carry a `content` collection, and a path like `@Node/content/billing.xlsx` reaches the file directly. On read, registered transformers make it *legible*: `.xlsx` becomes markdown tables (one per sheet), `.pdf` becomes page text, `.docx` becomes markdown — so "check the numbers in billing.xlsx" resolves to a file the agent can actually read and reason over, with no upload step and no separate file API. The same `@@content/…` reference **embeds** the file inline in a document. See [Collection Prefix](/Doc/DataMesh/UnifiedPath/ContentPrefix).

---

# The same move, everywhere else

The seven steps above are all instances of one pattern — *name the object, act on it* — and the pattern keeps paying off well beyond a single edit:

| The user wants | The operation | What's addressed |
|---|---|---|
| *"Show me this as a chart."* | embed / render a view — `@@Node/area/Chart` | a node's **layout area** — views are addressable and embeddable ([Area Prefix](/Doc/DataMesh/UnifiedPath/AreaPrefix)) |
| *"Ask the pricing model a question."* | `start_thread @Systemorph/Pricing/Q3` | an **agent / thread** anchored on the node — you can chat with any object ([Agentic AI](/Doc/Architecture/AgenticAI)) |
| *"Move this model under Archive."* | `move` / `copy` / `create` / `delete` by path | the **write target** — the address *is* the mutation target, routed to the owning hub |
| *"Who is allowed to change this?"* | read the `_Access` satellite on the path | **access** — permissions hang off the node's own address ([Access Control](/Doc/Architecture/AccessControl)) |
| *link it · autocomplete it · open it · call a tool on it* | the **same path** in each surface | one address works in markdown, the URL bar, autocomplete, the REST API, and agent tool calls |

---

# Why it matters

Because every object — the record, its schema, the model behind it, its views, its files, its access, the agent that reasons over it — is addressed the *same* way, a plain-English request can become a precise, typed, permission-checked action with no bespoke glue between steps. The agent knows **where it is** (1), can read the **data** (2) and the **schema** (3) that is guaranteed to match the **database** (4), resolve **dimensions** (5) against **meaningfully ranked** search (6), and reach the very **files** the user names (7) — then write the change back to the one authoritative address.

Addressability is the quiet invariant underneath all of it: **one namespace, one way to name anything, every surface speaking the same addresses.**

## Related pages

- [Unified Path](/Doc/DataMesh/UnifiedPath) — the `@` / `@@` addressing syntax this page builds on
- [Query Syntax](/Doc/DataMesh/QuerySyntax) — how to search the namespace
- [Query Result Scoring](/Doc/Architecture/QueryResultScoring) · [Vector Search](/Doc/Architecture/VectorSearch) — how results are ranked
- [Creating Node Types](/Doc/DataMesh/CreatingNodeTypes) · [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — the C# ↔ schema ↔ database chain
- [Access Control](/Doc/Architecture/AccessControl) — how access is addressed
