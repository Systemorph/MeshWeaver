# MeshWeaver.ContentCollections.Indexing.PostgreSql

PostgreSQL storage for the MeshWeaver content indexing pipeline. Stores content chunks and
their embeddings in pgvector and serves chunk-level vector search.

## Features

- `PostgreSqlChunkedContentVectorStore` — pgvector-backed chunk store with cosine search
- `PostgreSqlContentChunkSchema` — schema management for the chunk tables
- `EmbeddingProviderChunkEmbedder` — bridges `IEmbeddingProvider` (see `MeshWeaver.Hosting.Embeddings`) into the indexing pipeline
- `PostgreSqlContentIndexingExtensions` — one-call registration

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
