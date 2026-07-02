---
Name: Authoring Documentation
Category: Architecture
Description: How to write doc pages that render correctly — node paths, the link rules pinned by DocumentationLinkIntegrityTest, frontmatter, images, executable code blocks.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"/></svg>
---

# Authoring Documentation

Documentation lives as markdown files under `src/MeshWeaver.Documentation/Data/` and is served as mesh nodes under the **`Doc`** partition. The file system maps 1:1 to node paths — and links resolve against **node paths at render time**, not against the file system. Getting that one idea right is most of this page.

## Files → node paths

| File | Node path |
|---|---|
| `Data/Architecture.md` | `Doc/Architecture` |
| `Data/Architecture/AsynchronousCalls.md` | `Doc/Architecture/AsynchronousCalls` |
| `Data/GUI/ContainerControl/Stack.md` | `Doc/GUI/ContainerControl/Stack` |
| `Data/ReleaseNotes/3_0_0/index.md` | `Doc/ReleaseNotes/3_0_0` (an `index.md` *is* its folder's node) |

Note the pairing: `Architecture.md` is the **index node** and the folder `Architecture/` holds its children. Agent definitions follow the same scheme under the **`Agent`** partition (`src/MeshWeaver.AI/Data/Agent/Researcher.md` → `Agent/Researcher`), and skills under the **`Skill`** partition (`src/MeshWeaver.AI/Data/Skill/code.md` → `Skill/code`).

## 🚨 Link rules — pinned by `DocumentationLinkIntegrityTest`

At render time, `LinkUrlCleanupExtension` resolves every markdown link with `PathUtils.ResolveRelativePath` against the page's **full node path**. The page is treated as a *container*, so:

| You want to link… | Write | Resolves to |
|---|---|---|
| Anywhere (the robust default) | `[CQRS](/Doc/Architecture/CqrsAndContentAccess)` | exactly that path |
| A sibling page | `[CQRS](../CqrsAndContentAccess)` | up one from `Doc/Architecture/ThisPage`, then down |
| Your own child page (from an index) | `[Stack](Stack)` | `Doc/GUI/ContainerControl/Stack` |
| Another area | `[Data Binding](/Doc/GUI/DataBinding)` | absolute — immune to moves of *this* page |
| An agent definition | `[Researcher](/Agent/Researcher)` | the `Agent` partition |
| A built-in skill | `[/code](/Skill/code)` | the `Skill` partition |

And the three forms that **never** resolve:

```markdown
[X](SiblingPage)          ❌ from a leaf page this resolves to ThisPage/SiblingPage
[X](xref:Architecture/X)  ❌ there is no xref: handler in the pipeline
[X](../SiblingPage.md)    ❌ node paths have no .md suffix
```

`DocumentationLinkIntegrityTest` (in `test/MeshWeaver.Documentation.Test`) resolves every link in every doc, agent, and skill page with the **real** `PathUtils` and fails the build naming the page, the literal URL, and the bad target — so a broken link never reaches the portal.

Two more link rules:

- **`@/path` is local-only.** `[text](@/Doc/X)` is fine — the renderer strips the `@`. But never put `@/` inside a raw HTML `href=""`; HTML passes through the renderer verbatim.
- **Links inside code spans/fences aren't links.** Write `` `[text](path)` `` when you mean to *show* link syntax.

## Frontmatter

```yaml
---
Name: Human-readable page title          # shown in catalogs and the TOC
Category: Architecture                   # grouping hint
Description: One-line summary — lands on the node's Description column and search.
Icon: <svg …/>                           # inline SVG or /static/… path
---
```

`Thumbnail`, `Authors`, `Tags`, and `Abstract` (alias of `Description`) are also supported — see `MarkdownFileParser.MarkdownFrontMatter`.

## Images and diagrams

- **Inline SVG** directly in the markdown is the house style for diagrams — it themes with `currentColor`, needs no asset pipeline, and renders identically in docs and thumbnails. Never put blank lines or HTML comments *inside* an `<svg>` block (markdown would split it).
- **Static images** go through a content collection: `![alt](images/foo.svg)` resolves via `ImgPathMarkdownExtension` to the page's static content.
- **Mermaid** fenced blocks render for sequence/flow diagrams where hand-drawn SVG is overkill.

## Code samples are executable

**When a doc page brings a code example, the example is executable.** A real, runnable sample is written as an executable fenced block, never a static fence — the reader sees the code, a **Run** button on the cell's toolbar, and the result **directly below the code** (the notebook-cell shape). Blocks auto-execute on page load and Run re-executes them on demand.

The fence syntax:

```text
    ```csharp --render MyDemo --show-code
    MeshWeaver.Layout.Controls.Stack
        .WithView(MeshWeaver.Layout.Controls.Markdown("**live!**"))
    ```
```

- `--render <AreaName>` executes the block in the kernel and streams its last expression into the named result area below the code — the reader sees the real control, not a screenshot.
- `--show-code` displays the source above the result (this is what turns the block into a full notebook cell with the Run toolbar). Omit it for a live demo whose code isn't the point.
- `--execute <id>` runs silently (setup code shared by later blocks on the same page — blocks on one page share a single kernel REPL session, in document order).
- The fence language flows onto the submission: ` ```python --render X` routes to the connected Python worker instead of the in-process Roslyn kernel.

**A plain fence is the explicit marker for a non-runnable fragment** — pseudo-code, wire shapes, framework source excerpts (handlers, layout-area methods, service registrations), bash. If a fence shows real, self-contained runnable code, make it executable.

**Every executable block is enforced by CI**: `DocExecutableBlocksTest` (in `test/MeshWeaver.Persistence.Test`) extracts every `--render`/`--execute` block from every embedded doc page and executes it through a real kernel session — a block that stops compiling fails the build naming the page and the block. The same test carries a coverage ratchet, so silently converting an executable block back to a prose-only fence is visible.

Use executable blocks for every UI claim a page makes — if the doc says a control renders, the doc should *render it*. Pages that define sample models back them with real code in `src/MeshWeaver.Documentation/` and tests in `test/MeshWeaver.Documentation.Test` (see [Business Rules](/Doc/Architecture/BusinessRules) for the canonical example).

## Style

- **Present tense, what exists.** Document the current surface; don't narrate deleted APIs or migration history.
- **Code-first.** Lead with the canonical snippet; prose explains *why*, tables summarise options.
- **One page, one scope.** If a page needs a "which page do I read" preamble, add the router table (see [Deployment](/Doc/Architecture/Deployment)) and keep each page's scope crisp.
- The catalog lists every child node automatically; the curated area index (`Architecture.md`, `DataMesh.md`, `GUI.md`, `AI.md`) is what readers actually navigate — add load-bearing pages to its topic map.
