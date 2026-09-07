---
Name: Content Indexing Activation
Category: Architecture
Description: Everything that must be true before a mesh actually indexes its content, and the four ways it can be configured correctly and still index nothing — each one measured on a live portal, each one green while it fails.
Icon: Search
---

# Content Indexing Activation

Content indexing is easy to configure and easy to believe you have configured. This page is the
end-to-end chain, written after a live investigation in which **every layer reported success and
nothing was indexed**. It complements [Vector Search](/Doc/Architecture/VectorSearch) (which explains how the query
routes) by answering the prior question: *why is there nothing in the index to route to?*

## The one honest signal

`search_chunks` tells you in one line, and it is the only place in the stack that does:

```json
{"searched": false, "error": "search-not-performed",
 "message": "Content indexing is not active on this deployment: … no embedding provider —
             Embedding:Endpoint plus Embedding:ApiKey for the cloud provider"}
```

🚨 **`"searched": false` is a FAILED sweep, not a clean one.** The envelope deliberately carries no
`count`, precisely so an absent field cannot be misread as "no matches". Treat it the way you would
treat a compiler that did not run.

## The chain, and where each link breaks silently

Indexing activates only when **all three** hold: a mesh database connection string, the
`ContentCollections.Indexing.PostgreSql` module landed, and an **embedding provider** registered.
Two of the three announce themselves. The third is where the failures live.

### 1. The provider is not registered → nothing is ever written

`AddEmbeddings` registers nothing when `Embedding:Endpoint` is empty, and the storage adapter then
substitutes `NullEmbeddingProvider`, which returns `null` and leaves `mesh_nodes.embedding` NULL.
Nothing errors. Every free-text query quietly takes the ILIKE path and matches nothing.

`EmbeddingCapabilityReporter` exists for exactly this and logs the decision at startup — read it
before believing a deployment is indexing:

```
Semantic (vector) search DISABLED — no Embedding:Endpoint is configured.
```

### 2. The model and the column disagree → it fails at FIRST INDEX

The pgvector HNSW index tops out at **2000 dimensions** for the `vector` type. `text-embedding-3-large`
returns **3072** and cannot be indexed; `embed-v-4-0` returns **1536** and can. The pairing looks
correct in config and in the vault, and fails only when the first row is indexed — the worst possible
place to discover it.

### 3. The provider is registered but sends the wrong request shape

Measured on one Azure AI Services resource, 2026-09-06 — the same endpoint, the same key, two models:

| model | `input` as bare string | `input` as array | dimensions |
|---|---|---|---|
| `text-embedding-3-large` | 200 | 200 | 3072 |
| `embed-v-4-0` | **422** | 200 | 1536 |

So a provider that sends a bare string works with the model that cannot be indexed and fails with the
model that can. Through the portal this surfaced as an unhandled `HttpRequestException: 400`, which
is *better* than the silent cases only because something threw.

### 4. The provider works, and the back catalogue is still empty

A node's vector is computed **at write time**. On the day you switch a provider on, every row written
before that has a NULL embedding, and one job fills them in: `MeshNodeEmbeddingBackfill`, which runs
inside the **migration**, walks every schema holding `mesh_nodes`, reconciles the column to the
provider's dimension, and embeds every NULL row.

The migration must therefore have the *same* embedding configuration as the portal. When it does not,
it does not fail — it skips, and reports success:

```
[DocBackfill] 1242 documentation pages found (embeddings OFF — FTS only)
[DocBackfill] done: 161 upserted (0 embedded), 1081 unchanged, 1242 total
[EmbeddingBackfill] no embedding provider configured — skipped (hybrid/FTS search only)
Database migration completed. Version: 55
```

Hybrid recall softens this — a bare-text query ORs in a lexical `LIKE` on name/id/description, so
un-embedded rows do not vanish — which is also why the gap is easy to miss: search still returns
*something*, just never anything semantic.

## Two traps that produce a half-configured fleet

**A vault object added to a live SecretProviderClass reaches only pods that start afterwards.**
`envFrom` is resolved once, at pod start; the CSI driver refreshes the synced Secret on its own
rotation poll. Two pods of one ReplicaSet — same spec, same image — disagreed:

```
…-k8qlc  start 18:54:57Z   Embedding__ApiKey ABSENT
…-xzp5d  start 18:57:30Z   Embedding__ApiKey PRESENT
```

Half the replicas could index and half answered `searched:false`, which reads as an intermittent
feature rather than a configuration error. **After adding a vault object, roll the deployment** so
every replica re-resolves `envFrom`; a green deploy does not imply it.

**The `ApiKey` is optional for the OpenAI-compatible provider.** Without one it sends a dummy bearer,
so configuring `Provider`/`Endpoint`/`Model` *without* the key is worse than configuring nothing: a
provider registers, every call 401s, and the backfill logs-and-skips per row — still green, still
nothing embedded.

## Ordering

1. Land the fix your model needs **in the deployed image** before creating the vault key. A key that
   arrives first turns a clean "not active" envelope into a live exception on every query.
2. Give the **migration** the same `Embedding__*` config and the same key route as the portal.
3. Roll so every replica re-resolves `envFrom`.
4. Backfill: node vectors come from the migration's `MeshNodeEmbeddingBackfill`; **content chunks are
   separate** and are driven from the portal — Settings → Content Indexing → *Re-index all content*
   (`ContentIndexingObserver.ReindexAll`). Indexing is otherwise change-driven (`OnUploaded`), so
   existing files are never picked up on their own.
5. Verify with `search_chunks`: `searched:true` and real hits. Nothing else in the stack is evidence.

🚨 `ReindexAll` anchors its activity at `PartitionOf(collectionPaths.First())`, so collections must be
grouped **by partition** — one activity per partition, never one global call. A Space's collection
path is `{spacePath}/content`.

## Why an inert index can also be a *delivery* problem

Everything above assumes the portal can adopt what the registry publishes. When bundles are **not
sealed for the framework identity an instance runs**, the instance adopts nothing and recompiles
every NodeType at boot from mesh source — visible as
`N adoption attempt(s), 0 assembly/assemblies adopted, N MISS(es)`. Any drift in that source then
becomes a compile error, the `nodetype_bake` health check refuses readiness, and the instance cannot
take a new image at all — including one carrying the embedding fix. At that point the indexing
problem is downstream of a delivery problem, and no amount of `Embedding__*` config will move it.

See [Module Versioning](/Doc/Architecture/ModuleVersioning) for what sealing means, and
[Vector Search](/Doc/Architecture/VectorSearch) for the query side.
