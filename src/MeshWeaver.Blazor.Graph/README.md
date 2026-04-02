# MeshWeaver.Blazor.Graph

Provides Blazor Razor components for viewing and editing MeshWeaver graph nodes, including a Monaco-based node editor with metadata and content editing.

## Features

- `MeshNodeEditorView` -- full node editor with path manipulation, metadata saving, and content editing via Monaco editor
- `MeshNodeThumbnailView` -- compact thumbnail rendering for node cards
- `MeshNodeCardView` -- card-style node display
- Autocomplete support for `@` references and frontmatter fields in the editor
- Node move/rename via `MoveNodeRequest` and content updates via `UpdateNodeRequest`

## Usage

```csharp
// Register graph Blazor views on the message hub
configuration.AddGraphViews();
```

This registers `MeshNodeEditorControl`, `MeshNodeThumbnailControl`, and `MeshNodeCardControl` with their corresponding Blazor view components and enables `@` autocomplete in markdown editors.

## Dependencies

- `MeshWeaver.Blazor` -- base Blazor component infrastructure
- `MeshWeaver.Graph` -- graph node types, controls, and navigation
- `MeshWeaver.ContentCollections` -- embedded resource serving for node icons
- `Microsoft.FluentUI.AspNetCore.Components` -- Fluent UI component library
