# MeshWeaver.Markdown.Collaboration

Provides the anchoring infrastructure for collaborative markdown: the version-delta position engine
that anchors comments to clean text and relocates history-derived changes, plus the marker utilities
that keep legacy content clean.

## Features

- `AnchorMath` -- the version-delta engine. Anchors a captured `[start, length)` range to a known
  text version and recomputes its effective range against the current text by diffing the two
  versions (`diff_xIndex`-style position mapping). This is what lets comments live as satellites
  with the document kept clean, and it is also the diff engine behind `ChangeProjection` (the
  tracked-change view model derived from the version history); see `CommentRendering` /
  `ChangeRendering` / `ChangeProjection` in `MeshWeaver.Graph`.
- `MarkdownAnnotationParser` -- extracts/strips annotation markers (`<!--comment:id-->…`). Used to
  keep rendered content clean; the comment/change flow no longer *embeds* markers.
- `CollaborativeEditingMessages` -- the `CreateCommentRequest` message contract handled by
  `AddComments()` in `MeshWeaver.Graph`. (There is no suggest-edit request: a suggested edit is a
  normal versioned write through `GetMeshNodeStream(path).Update(...)`.)
- `AnnotationSyncService` / `TrackedChange` -- the legacy marker-based model still used by the
  Monaco `Suggest` editing surface. Distinct from `MeshWeaver.Mesh.TrackedChange`, which is the
  view model `ChangeProjection` produces.

The unused Operational-Transformation layer (`CollaborativeEditingCoordinator`, `TextOperation*`,
`DocumentState`) and the legacy `RangeComment` model were removed — nothing in production reached
them.

## Dependencies

- `MeshWeaver.Data.Contract` -- data contract types
- `MeshWeaver.Messaging.Contract` -- messaging primitives
