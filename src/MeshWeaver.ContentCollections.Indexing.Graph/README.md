# MeshWeaver.ContentCollections.Indexing.Graph

Mesh/portal integration for the MeshWeaver content indexing pipeline. Watches content
collections for changes, runs indexing as mesh activities, and surfaces the index in the
portal (autocomplete, settings).

## Features

- `ContentIndexingObserver` / `ContentIndexingActivity` — change-driven re-indexing as activity-control-plane operations
- `ContentChunkAutocompleteProvider` — chunk search results in `@` autocomplete
- `ChatClientSummarizer` / `ChatClientImageDescriber` — AI-generated summaries and image descriptions for indexed content
- `ContentIndexSettingsTab` — per-space index configuration in the portal settings

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
