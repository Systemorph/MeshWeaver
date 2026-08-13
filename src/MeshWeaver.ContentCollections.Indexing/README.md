# MeshWeaver.ContentCollections.Indexing

Core content-to-vector indexing pipeline for MeshWeaver content collections. Splits content
into chunks, tracks per-file `Document` records, and exposes chunk-level search abstractions.
This package is storage- and embedding-agnostic — pair it with
`MeshWeaver.ContentCollections.Indexing.PostgreSql` (pgvector store) and
`MeshWeaver.ContentCollections.Indexing.Graph` (portal integration).

## Features

- `ContentIndexingService` — the chunk/embed/store pipeline over content collections
- `ContentChunk` / `ChunkPosition` — chunk model with stable positions into the source file
- `Document` / `DocumentInfo` — per-file index bookkeeping
- `ContentChunkSearch` — search API over indexed chunks
- `ContentIndexingOptions` — chunking and indexing configuration

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
