# MeshWeaver.Markdown.Collaboration

Provides collaborative editing infrastructure for markdown documents, including Operational Transformation (OT) for concurrent edits, range-anchored comments, and tracked changes with accept/reject workflows.

## Features

- `CollaborativeEditingCoordinator` -- manages document state, applies OT transformations, and tracks active editing sessions with cursor positions
- Text operations: `InsertOperation`, `DeleteOperation`, `CompositeOperation` with version-based conflict resolution
- `TextOperationTransformer` for server-side OT transformation of concurrent edits
- `RangeComment` -- comments anchored to text ranges via embedded markdown markers (`<!--comment:MarkerId-->`)
- `TrackedChange` -- suggested edits (insertions, deletions, replacements) with pending/accepted/rejected status
- `AnnotationSyncService` for synchronizing annotations between the document and external storage
- `MarkdownAnnotationParser` for extracting and embedding annotation markers in markdown content
- Vector clock-based versioning and session presence awareness with stale session cleanup

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
