# MeshWeaver.Hosting.Sqlite

SQLite storage backend for MeshWeaver. Single-file, zero-infrastructure persistence for
development, testing, and edge deployments — including local vector search.

## Features

- `SqliteStorageAdapter` / `SqlitePartitionStorageProvider` — mesh node persistence per partition
- `SqliteEventLogStore` — the mesh event/version log
- `SqliteVectorMeshQuery` — vector-backed free-text mesh search on SQLite
- `ITextEmbedder` with `OllamaTextEmbedder` — local embeddings for the vector index
- `SqliteExtensions` — one-call registration

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
