# MeshWeaver.Hosting.Embeddings

Embedding provider abstraction for MeshWeaver. Supplies the text-embedding capability used
by mesh vector search and content indexing, with implementations for hosted and local models.

## Features

- `IEmbeddingProvider` — the single embedding abstraction the mesh consumes
- `AzureFoundryEmbeddingProvider` — Azure AI Foundry embedding models
- `OllamaEmbeddingProvider` — local models via Ollama
- `NullEmbeddingProvider` — explicit no-embedding fallback (structured search only)
- `EmbeddingExtensions` / `EmbeddingOptions` — registration and configuration

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
