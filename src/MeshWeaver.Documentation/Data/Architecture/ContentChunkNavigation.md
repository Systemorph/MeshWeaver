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

Chunks live in a per-partition Postgres `content_chunks` table — one schema per partition, exactly like `mesh_nodes` and the satellite tables (see [Postgres Schema Architecture](../PostgresSchemaArchitecture)). The columns are `collection_path, file_path, chunk_index, content_hash, chunk_text, metadata, embedding, page, bbox, last_modified`. The in-memory store (`InMemoryChunkedContentVectorStore`) backs the unit-tested core with the identical contract.

The `page` + `bbox` columns are **source provenance** — see [Source provenance](#source-provenance-page--position) below. They are added by an idempotent `ADD COLUMN IF NOT EXISTS` in the (self-provisioning, per-partition) schema script, so an existing partition gains them on its next provision — there is no separate `DbVersion` migration.

## Retrieval vs. extraction

There are two different jobs, and they want different granularities:

- **Retrieval / context** — *find* the relevant passage and feed it to the model. Chunks are perfect: a 1000-char window is a model-sized unit of context, and similarity ranks the most relevant windows first. Use `search_chunks` + `get_chunk`.
- **Extraction** — pull a *whole* structure (a table, a full section, a complete document) where chunk boundaries would cut a row in half. Read the whole document, not chunks. **Get the file path, not the `Document` node** — see the note below.

> **🚨 Full text lives at the file path, not the `Document` node.** A search hit is the `Document` node at `{collectionPath}/_Documents/{slug}`, and `Get` on **that** returns only the node's metadata + the AI `Summary` (~a few hundred chars) — never the text. The complete extracted text is served by `Get` on the **file path** `{collectionPath}/{filePath}`, through the registered content transformers (#396). Derive the file path from the hit's own `collectionPath` + `filePath` fields (`search_chunks` returns both; the `Document` node carries them too) — do **not** reconstruct it by editing the node path, because `{slug}` is a lossy encoding of `filePath` (directory separators and non-ASCII collapse to `-`). Getting the `Document` node, seeing only a summary, and concluding "the full text is unavailable" is the failure that sends an agent stepping through every chunk — exactly the chunk-boundary corruption this section warns against.

The chunk tools are deliberately **not** a substitute for a full-document read. If you need every row of a table, a windowed chunk is the wrong tool — it may start or end mid-table.

## The three tools

| Tool | Granularity | Returns | Use when |
|---|---|---|---|
| `search` | **Document node** | the file's `Document` node (chunk index dropped) | "which file is about X?" — navigation, linking |
| `search_chunks` | **chunk** | `{documentPath, collectionPath, filePath, chunkIndex, rank, snippet, page?, bbox?}` per hit | "find the passages about X" — gather context |
| `get_chunk` | **one chunk** | `{text, prevIndex, nextIndex, totalChunks, page?, bbox?, …}` | read a known chunk + step to neighbours |

### `search` — Document-level

Node `search` (and the autocomplete `document:` prefix) runs the same cosine search over chunks but resolves each hit **up to its `Document` node** via `DocumentPaths.For(collectionPath, filePath)` and dedupes by file — one result per file, keeping the best-scoring chunk's snippet. The chunk index is discarded. This is the right answer for "take me to the file". On Postgres, bare-text tokens in a node `search` route through the same HNSW vector path described in [Vector Search](../VectorSearch).

### `search_chunks` — chunk-level

`search_chunks(query, scope?, limit=20)` embeds the query and runs the cosine search across the in-scope collection(s), but keeps the chunk coordinate. Results are **not** deduped by file — chunk-level granularity is the whole point — and are capped at `limit`. Each hit carries the `(collectionPath, filePath, chunkIndex)` triple you feed back into `get_chunk`, plus a `documentPath` for linking and a `rank` (0-based, best-first). The store returns hits most-similar-first but does not surface the raw cosine distance, so `rank` is the relevance signal rather than a fabricated score.

**Two scope models** — the engine (`ContentChunkSearch` in the indexing core) dispatches by query shape:

| Form | How collections are resolved | Use when |
|---|---|---|
| **Anchored** — `scope` is a node path | the scope path itself plus each *ancestor* prefix (`part/Space/Sub` → `part/Space/Sub`, `part/Space`, `part`) | you don't know exactly which collection holds the content — "what's relevant to where I am" (the agent's context anchor) |
| **Targeted** — `namespace:<node>/<collection>` in the query | the one named collection, resolved by an optional `scope:` qualifier | you know the collection and want only it |

For the targeted form the `scope:` qualifier selects:

- **`scope:subtree`** — the named collection plus every collection nested under it. This is the **default** when a `namespace:` is given and no `scope:` is written ("check only this collection [and anything below it]"), and it maps to the store's `SearchSubtree` (`collection_path = ns OR collection_path LIKE ns/%`, a single-schema prefix predicate — every nested collection shares the partition's first-segment schema).
- **`scope:exact`** — only the exact named collection.
- **`scope:ancestors` / `scope:ancestorsandself`** — reproduce the anchored ancestor walk from the named path.

When a query carries a `namespace:` token the targeted form wins and the `scope` *parameter* is ignored. With neither a `scope` path nor a `namespace:` token there is no collection to anchor on, so the tool returns an empty result with a hint to pass one rather than guessing.

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
  "totalChunks": 12,
  "page": 4,
  "bbox": { "x": 0.12, "y": 0.34, "w": 0.61, "h": 0.08 }
}
```

`prevIndex` is `null` at index 0; `nextIndex` is `null` at the last chunk; `totalChunks` lets the caller bound the range. An out-of-range index (or a file that was never indexed) returns `{found:false, totalChunks, message}` carrying the valid range, never an error. This is how an agent reads a `search_chunks` hit in full and then walks forward or backward through the document a window at a time. `page` + `bbox` are present only when the source carried a layout (PDFs) — see [Source provenance](#source-provenance-page--position).

## Where the tools live

The search itself is one engine — `ContentChunkSearch` in the storage-agnostic indexing core (`MeshWeaver.ContentCollections.Indexing`). Every surface calls it, so they stay in sync by construction — including the GUI's "this maps to a tool call" claim, which is honest precisely because the GUI runs the same code the agent does:

- **Agent tools** — the `ContentCollection` plugin (`ContentCollectionPlugin`) exposes `search_chunks` and `get_chunk` alongside `UploadContent`. Any agent that declares the `ContentCollection` plugin gets them automatically.
- **MCP tools** — `McpMeshPlugin` exposes the same two as `search_chunks` / `get_chunk`. Both agent and MCP surfaces go through the thin `ChunkNavigation` adapter (`MeshWeaver.AI`), which resolves the store + embedder from the session hub's services and delegates to `ContentChunkSearch`.
- **GUI** — the **Content Indexing** settings tab (on Space nodes, `ContentIndexSettingsTab`) has an *Explore index* search box that calls `ContentChunkSearch` directly. See below.

## The Explore-index GUI

The Content Indexing settings tab is the in-portal surface for the index. Besides status + "Re-index all content", its **Explore index** section lets a user search the space's content interactively:

- A search box **pre-filled** with `namespace:{space}/content scope:subtree ` — the targeted form scoped to this space's collection. The user appends free-text terms; results update live (throttled, with the superseded search dropped).
- **Ranked hits** as clickable rows (file · chunk index · snippet). Clicking one opens an expandable reader that calls `get_chunk` and steps prev/next through the file.
- A **tool-call inspector**: each search shows the exact `search_chunks(query: "namespace:… scope:subtree …", limit: N)` it maps to, and an opened chunk shows its `get_chunk(collectionPath, filePath, chunkIndex)`. Because the box drives the *same* `ContentChunkSearch` engine the agent tools use, the displayed call is the real one — a way to see how the agent retrieves content, and to debug what a given query actually matches.

The store reads (`GetChunk`, `GetChunkCount`) are on `IChunkedContentVectorStore`. Like every read in the indexing core they are reactive and cold (`IObservable<T>`), and the Postgres implementation runs the DB round-trip through the cap-1 `pg:vector` I/O pool (see [Controlled I/O Pooling](../ControlledIoPooling)) — never a bare `Observable.FromAsync`. When content indexing is not wired into a host, the store/embedder are absent and the tools degrade to a clear "not available" envelope instead of throwing.

## Source provenance (page + position)

Every chunk carries **where it came from** in the source document, so a consumer can cite the page and *open the source page and mark the exact region* — not just quote text.

- **`page`** — the one-based source page the chunk begins on.
- **`bbox`** — the chunk's normalized bounding box on that page: `{x, y, w, h}`, each a fraction of the page in `[0,1]` with a **top-left origin**. Normalized so a viewer can overlay the highlight at any render scale without knowing the page's point size.

**Extraction is positional.** For PDFs the extractor (`TextExtractor`) builds the text word-by-word from PdfPig's laid-out words and records one span (page + box, in PDF points → normalized top-left) per word. The chunker (`TextChunker.ChunkPositioned`) then attributes each character window to the page it *begins* on and unions the boxes of the words inside the window on that page — so a chunk that straddles a line break still gets one tight box, and a chunk that crosses a page boundary is pinned to its starting page. Formats with no layout (txt/markdown/docx) carry a null `page`/`bbox` and degrade gracefully.

**Marking in the viewer.** The `Document` node's **Source** area renders the original PDF (PDF.js) and, given the deep-linked chunk's `page` + `bbox`, scrolls to that page and overlays a highlight rectangle at the exact region — precise and robust, independent of whether the chunk text can be re-found by string match. When the position is absent it falls back to the verbatim text-match highlight. The block reader also shows `· page N` in each block's header.

**Backfilling existing collections.** The plain re-index is hash-gated — an unchanged file is skipped, so it would never gain provenance. The Content Indexing tab's **Rebuild** button (and `ContentIndexingObserver.ReindexAll(..., force: true)`) bypasses the hash gate to re-extract, re-chunk and re-store every file, populating `page`/`bbox` on files indexed before the feature existed.

## Reading a document end to end

A typical agent loop:

1. `search_chunks("accrued benefit obligation", scope: "ACME/Reports")` → a ranked list of chunk hits.
2. Pick the top hit's `(collectionPath, filePath, chunkIndex)`.
3. `get_chunk(...)` to read the full window, then follow `nextIndex` / `prevIndex` to read the surrounding context.
4. If the goal is to extract a whole table rather than gather context, `Get` the **file path** `{collectionPath}/{filePath}` (built from the hit's own fields) for the complete text — **not** the hit's `documentPath` (the `Document` node), which returns only metadata + summary (see the note under "Retrieval vs. extraction").

Related: [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) for read semantics, [Vector Search](../VectorSearch) for the node-level semantic path.
