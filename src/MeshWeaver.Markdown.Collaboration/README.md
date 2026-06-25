# MeshWeaver.Markdown.Collaboration

Provides collaborative editing infrastructure for markdown documents: the version-delta position engine that anchors comments and tracked changes to clean text, Operational Transformation (OT) for concurrent edits, and the marker utilities used to keep legacy content clean.

## Features

- `AnchorMath` -- the version-delta engine. Anchors a captured `[start, length)` range to a known text version and recomputes its effective range against the current text by diffing the two versions (`diff_xIndex`-style position mapping). This is what lets comments/changes live as satellites with the document kept clean; see `CommentRendering` / `ChangeRendering` in `MeshWeaver.Graph`.
- `CollaborativeEditingCoordinator` -- manages document state, applies OT transformations, and tracks active editing sessions with cursor positions
- Text operations: `InsertOperation`, `DeleteOperation`, `CompositeOperation` with version-based conflict resolution
- `TextOperationTransformer` for server-side OT transformation of concurrent edits
- `MarkdownAnnotationParser` -- extracts/strips annotation markers (`<!--comment:id-->…`). Used to keep rendered content clean; the comment/change flow no longer *embeds* markers.
- `RangeComment` -- a legacy marker-anchored comment model, superseded by the satellite + `AnchorMath` model.

## Usage

```csharp
var coordinator = new CollaborativeEditingCoordinator();

// Initialize a document
coordinator.InitializeDocument("doc-1", "# Hello World");

// Apply an edit
var result = coordinator.ApplyOperation("doc-1",
    new InsertOperation { Position = 13, Text = "\nNew line", UserId = "user1" },
    currentContent: "# Hello World");
```

## Dependencies

- `MeshWeaver.Data.Contract` -- data contract types
- `MeshWeaver.Messaging.Contract` -- messaging primitives
