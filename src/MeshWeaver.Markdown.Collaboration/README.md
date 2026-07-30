# MeshWeaver.Markdown.Collaboration

Provides the anchoring infrastructure for collaborative markdown: the version-delta position engine
that anchors comments and tracked changes to clean text, plus the marker utilities that keep legacy
content clean.

## Features

- `AnchorMath` -- the version-delta engine. Anchors a captured `[start, length)` range to a known
  text version and recomputes its effective range against the current text by diffing the two
  versions (`diff_xIndex`-style position mapping). This is what lets comments/changes live as
  satellites with the document kept clean; see `CommentRendering` / `ChangeRendering` in
  `MeshWeaver.Graph`.
- `MarkdownAnnotationParser` -- extracts/strips annotation markers (`<!--comment:id-->…`). Used to
  keep rendered content clean; the comment/change flow no longer *embeds* markers.
- `CollaborativeEditingMessages` -- the `CreateCommentRequest` / `CreateSuggestedEditRequest`
  message contracts handled by `AddComments()` / `AddTracking()` in `MeshWeaver.Graph`.
- `AnnotationSyncService` / `TrackedChange` -- the legacy marker-based model still used by the
  Monaco `Suggest` editing surface.

The unused Operational-Transformation layer (`CollaborativeEditingCoordinator`, `TextOperation*`,
`DocumentState`) and the legacy `RangeComment` model were removed — nothing in production reached
them.

## Dependencies

- `MeshWeaver.Data.Contract` -- data contract types
- `MeshWeaver.Messaging.Contract` -- messaging primitives
