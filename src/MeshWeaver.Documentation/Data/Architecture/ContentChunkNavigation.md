---
Name: Content Chunk Navigation
Category: Architecture
Description: How indexed content is chunked into overlapping windows, and the retrieval tools — search (Document nodes), search_chunks (chunk-level hits with index), and get_chunk (prev/next stepping) — that read those chunks.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 4h16v4H4z"/><path d="M4 12h16v4H4z"/><path d="M4 20h10"/></svg>
---

# Content Chunk Navigation

When a content file is indexed, its extracted text is sliced into overlapping **chunks** and each chunk is embedded for similarity search. Most retrieval surfaces hide the chunk and answer at the *file* level — "which Document matches?" — which is the right answer for navigation. This page covers the layer underneath: reading the chunks themselves, by similarity and by index, so an agent can pull the exact passage it needs and step through a file's neighbouring windows.

## The chunking model

`ContentIndexingService` splits each file's extracted text into fixed-size character windows:

| Property | Value |
|---|---|
| Window size | **1000 characters** |
| Overlap | **150 characters** (each window repeats the trailing 150 chars of the previous one) |
| Index | **`chunk_index`** — 0-based, per file, contiguous |

The overlap means a sentence straddling a window boundary still lands wholly inside at least one chunk, so a semantic hit is never split across two windows. Every chunk of one file carries the same whole-file `content_hash` (the hash gate's "did this file change?" key); per-chunk identity is `chunk_index`.

Chunks live in a per-partition Postgres `content_chunks` table — one schema per partition, exactly like `mesh_nodes` and the satellite tables (see [Postgres Schema Architecture](../PostgresSchemaArchitecture)). The columns are `collection_path, file_path, chunk_index, content_hash, chunk_text, metadata, embedding, last_modified`. The in-memory store (`InMemoryChunkedContentVectorStore`) backs the unit-tested core with the identical contract.

## Retrieval vs. extraction

There are two different jobs, and they want different granularities:

- **Retrieval / context** — *find* the relevant passage and feed it to the model. Chunks are perfect: a 1000-char window is a model-sized unit of context, and similarity ranks the most relevant windows first. Use `search_chunks` + `get_chunk`.
- **Extraction** — pull a *whole* structure (a table, a full section, a complete document) where chunk boundaries would cut a row in half. Read the whole document, not chunks. Use `Get` on the file's `Document` node (or the raw `content/` file).

The chunk tools are deliberately **not** a substitute for a full-document read. If you need every row of a table, a windowed chunk is the wrong tool — it may start or end mid-table.

## The three tools

| Tool | Granularity | Returns | Use when |
|---|---|---|---|
| `search` | **Document node** | the file's `Document` node (chunk index dropped) | "which file is about X?" — navigation, linking |
| `search_chunks` | **chunk** | `{documentPath, collectionPath, filePath, chunkIndex, rank, snippet}` per hit | "find the passages about X" — gather context |
| `get_chunk` | **one chunk** | `{text, prevIndex, nextIndex, totalChunks, …}` | read a known chunk + step to neighbours |

### `search` — Document-level

Node `search` (and the autocomplete `document:` prefix) runs the same cosine search over chunks but resolves each hit **up to its `Document` node** via `DocumentPaths.For(collectionPath, filePath)` and dedupes by file — one result per file, keeping the best-scoring chunk's snippet. The chunk index is discarded. This is the right answer for "take me to the file". On Postgres, bare-text tokens in a node `search` route through the same HNSW vector path described in [Vector Search](../VectorSearch).

### `search_chunks` — chunk-level

`search_chunks(query, scope?, limit=20)` embeds the query and runs the cosine search across the in-scope collection(s), but keeps the chunk coordinate. Results are **not** deduped by file — chunk-level granularity is the whole point — and are capped at `limit`. Each hit carries the `(collectionPath, filePath, chunkIndex)` triple you feed back into `get_chunk`, plus a `documentPath` for linking and a `rank` (0-based, best-first). The store returns hits most-similar-first but does not surface the raw cosine distance, so `rank` is the relevance signal rather than a fabricated score.

**Scope** is the ancestor-prefix walk: the scope path itself plus each parent prefix. A scope of `part/Space/Sub` searches collections keyed at `part/Space/Sub`, `part/Space`, and `part`. With no scope (and no agent context), there is no collection to anchor on, so the tool returns an empty result with a hint to pass one rather than guessing.

### `get_chunk` — read + step

`get_chunk(collectionPath, filePath, chunkIndex)` reads exactly one chunk and reports its neighbours:

```json
{
  "found": true,
  "collectionPath": "ACME/Reports",
  "filePath": "pension/2025.txt",
  "chunkIndex": 4,
  "text": "…the full 1000-char window…",
  "prevIndex": 3,
  "nextIndex": 5,
  "totalChunks": 12
}
```

`prevIndex` is `null` at index 0; `nextIndex` is `null` at the last chunk; `totalChunks` lets the caller bound the range. An out-of-range index (or a file that was never indexed) returns `{found:false, totalChunks, message}` carrying the valid range, never an error. This is how an agent reads a `search_chunks` hit in full and then walks forward or backward through the document a window at a time.

## Where the tools live

Both surfaces share one implementation (`ChunkNavigation` in `MeshWeaver.AI`), so the agent and MCP transports stay in sync by construction:

- **Agent tools** — the `ContentCollection` plugin (`ContentCollectionPlugin`) exposes `search_chunks` and `get_chunk` alongside `UploadContent`. Any agent that declares the `ContentCollection` plugin gets them automatically.
- **MCP tools** — `McpMeshPlugin` exposes the same two as `search_chunks` / `get_chunk`, resolving the store and embedder from the MCP session hub's services.

The store reads (`GetChunk`, `GetChunkCount`) are on `IChunkedContentVectorStore`. Like every read in the indexing core they are reactive and cold (`IObservable<T>`), and the Postgres implementation runs the DB round-trip through the cap-1 `pg:vector` I/O pool (see [Controlled I/O Pooling](../ControlledIoPooling)) — never a bare `Observable.FromAsync`. When content indexing is not wired into a host, the store/embedder are absent and the tools degrade to a clear "not available" envelope instead of throwing.

## Reading a document end to end

A typical agent loop:

1. `search_chunks("accrued benefit obligation", scope: "ACME/Reports")` → a ranked list of chunk hits.
2. Pick the top hit's `(collectionPath, filePath, chunkIndex)`.
3. `get_chunk(...)` to read the full window, then follow `nextIndex` / `prevIndex` to read the surrounding context.
4. If the goal is to extract a whole table rather than gather context, switch to `Get` on the hit's `documentPath` for the complete document.

Related: [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) for read semantics, [Vector Search](../VectorSearch) for the node-level semantic path.
