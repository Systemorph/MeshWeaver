---
Name: Commenting Reference
Category: Documentation
Description: How to programmatically add comments and suggest edits on Markdown documents
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>
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

Comments are satellite entities in the `_Comment` partition — the document text is **never modified**. The tool:
1. Finds the selected text in the document's clean content
2. Creates a Comment entity that captures the character range (`Start`/`Length`), the document `Version`, and the text at that version (`AnchorText`)
3. The highlight is re-derived at render time from the satellite — and follows its text when the document is edited above it (the range is recomputed via the version delta), so a comment never needs write access to the document

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

A tracked change is a satellite entity in the `_Tracking` partition — like a comment, it does **not** alter the document. It captures the affected range plus the suggested text (`Insertion`, `Deletion`, or `Replacement`), and the document shows an inline **diff** re-derived from the satellite. Other collaborators accept or reject it: **accepting** applies the suggested text to the document, **rejecting** drops the satellite and leaves the document unchanged.

## Important Notes

- **`documentPath` is a path, not a name.** Pass the canonical node path (`@/PartnerRe/AIConsulting/FinalReport`, or `@FinalReport` if relative to the current context). Passing the node's display name ("Final Report – AI Readiness Assessment & 100-Day Plan") will silently fail — the request routes to a non-existent grain. If you only know the display name, `Search('name:"..."')` first and use the `path` field of the match.
- Same `@/<path>` = absolute, `@<path>` = relative-to-context rule as every other tool (see Tools Reference → Path Rules).
- The `selectedText` / `originalText` must be an **exact match** of text in the document.
- Use `Get` first to retrieve the document and verify the text exists.
- Comments and edits are persisted as satellite entities and visible to all collaborators.
