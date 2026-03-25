---
Name: Commenting Reference
Category: Documentation
Description: How to programmatically add comments and suggest edits on Markdown documents
---

## Adding Comments

Use the **AddComment** tool to add a comment anchored to a specific text passage in a Markdown document.

### Parameters
- `documentPath` — Path to the document (e.g., `@org/MyDoc`)
- `selectedText` — The exact text passage to comment on (must match the document content)
- `commentText` — Your comment text

### Example

User says: "Add a comment on 'quarterly results' saying 'needs updated figures'"

1. First verify the document: `Get('@org/Report')`
2. Call: `AddComment('@org/Report', 'quarterly results', 'needs updated figures')`

### How It Works

Comments are stored as satellite entities in the `_Comment` partition. The tool:
1. Finds the selected text in the document's markdown content
2. Inserts inline markers: `<!--comment:id-->selected text<!--/comment:id-->`
3. Creates a Comment entity with the marker ID linking it to the text range

## Suggesting Edits (Track Changes)

Use the **SuggestEdit** tool to propose text changes without directly editing the document.

### Parameters
- `documentPath` — Path to the document
- `originalText` — The exact text to replace (empty string for pure insertion)
- `newText` — The replacement text (empty string for deletion)

### Examples

**Replace text**: `SuggestEdit('@org/Doc', 'old phrase', 'new phrase')`
**Delete text**: `SuggestEdit('@org/Doc', 'text to remove', '')`

### How It Works

Track changes use inline markers in the markdown:
- **Insertions**: `<!--insert:id:Author:Date-->new text<!--/insert:id-->`
- **Deletions**: `<!--delete:id:Author:Date-->deleted text<!--/delete:id-->`

The markers are visible to other collaborators who can accept or reject the changes.

## Important Notes

- The `selectedText` / `originalText` must be an **exact match** of text in the document
- Use `Get` first to retrieve the document and verify the text exists
- Comments and edits are persisted as satellite entities and visible to all collaborators
