---
Name: Code, Diff & Markdown Editors
Category: Documentation
Description: Monaco-backed editing controls — syntax-highlighted code, side-by-side diffs, and the markdown editor — rendered live.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg>
---

# Code, Diff & Markdown Editors

Three Monaco-backed controls cover editing and reviewing text content. They complement the
form-generating [Editor](../Editor) (which handles *structured records*): these handle *source
text* — code, markdown, and diffs.

| Control | Purpose |
|---|---|
| `CodeEditorControl` | Syntax-highlighted code editing/viewing |
| `DiffEditorControl` | Side-by-side original-vs-modified comparison |
| `MarkdownEditorControl` | Markdown editing with preview, track changes, and auto-save |

---

# Code Editor

`CodeEditorControl` embeds Monaco with full syntax highlighting. Set `Readonly` for display-only
snippets; leave it editable for authoring surfaces (the mesh's Code nodes use exactly this control).

```csharp --render CodeEditorDemo --show-code
new CodeEditorControl()
    .WithValue(
        "public record Position(string Name, double Amount);\n" +
        "\n" +
        "var totalAssets = positions\n" +
        "    .Where(p => p.Amount > 0)\n" +
        "    .Sum(p => p.Amount);")
    .WithLanguage("csharp")
    .WithReadonly(true)
    .WithHeight("160px")
    .WithLineNumbers(true)
    .WithMinimap(false)
```

Other options: `WithTheme(string)`, `WithWordWrap(bool)`, `WithPlaceholder(string)`.

---

# Diff Editor

`DiffEditorControl` renders the classic two-pane diff — the same view the mesh uses for node
version comparisons and tracked-change review.

```csharp --render DiffEditorDemo --show-code
new DiffEditorControl
{
    OriginalLabel = "2024 disclosure",
    ModifiedLabel = "2025 disclosure",
    Language = "markdown",
    Height = "180px",
    OriginalContent =
        "# Funding\n" +
        "The funding ratio stands at 109.8%.\n" +
        "Asset allocation is unchanged.",
    ModifiedContent =
        "# Funding\n" +
        "The funding ratio stands at 112.4%.\n" +
        "Equity allocation increased by 3 percentage points."
}
```

---

# Markdown Editor

`MarkdownEditorControl` is the markdown authoring surface: live preview, comments, and track
changes. Standalone it edits in memory; bound to a node via `WithAutoSave(hubAddress, nodePath)` it
persists every change through the node stream — the standard way to make markdown editing stick.

```csharp --render MarkdownEditorDemo --show-code
new MarkdownEditorControl()
    .WithValue(
        "# Meeting Notes\n\n" +
        "- Reviewed **Q2 results**\n" +
        "- Agreed to increase the *equity* allocation\n" +
        "- Next review: October")
    .WithHeight("260px")
```

> **Persisting edits.** For content that lives on a mesh node, always use
> `WithAutoSave(hubAddress, nodePath)` — the editor then writes through the node stream and every
> other view of the node updates live. Never copy node content into a layout-area data item and
> save it back with a subscription.

---

# See Also

- [Editor](../Editor) — auto-generated forms for structured records
- [Data Binding](../DataBinding) — how node-bound values flow into these controls
