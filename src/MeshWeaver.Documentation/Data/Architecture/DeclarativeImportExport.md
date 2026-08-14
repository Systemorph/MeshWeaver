# Declarative export and import

How a node type tells the platform what its content can DO — export to PDF, round-trip through
a git file — without the platform carrying a compiled `if (nodeType == …)` for it. The goal is
composition-first: a plugin pack declares; core mechanisms read declarations.

## Export: shipped (phase 1)

A node type declares its export behaviour on its own hub configuration:

```csharp
config.WithExport(ExportDeclaration.Document)    // PDF / Email / DOCX over the node's body
config.WithExport(ExportDeclaration.SlideDeck)   // PDF / Email, one page per slide
config.WithExport(new ExportDeclaration { Formats = ExportFormats.Pdf })
```

`ExportDeclaration` (MeshWeaver.Graph) rides the same `MessageHubConfiguration.Set` idiom as
`PageLayoutOptions`. Readers:

- **`ExportMenuProvider`** — THE export node-menu provider, one for all types. It resolves the
  declaration off the focused node's hub configuration and renders the Export group with exactly
  the declared entries. The former per-type providers (`MarkdownExportMenuProvider`,
  `DeckExportMenuProvider`) are deleted; a new exportable type declares instead of adding
  compiled code.
- **`ExportDocumentLayoutArea`** — offers the pixel-fidelity picker when the declared
  `Composition` is `SlideDeck`.

Because the declaration travels on the hub configuration, a PLUGIN type declares in its own
configuration lambda — in-mesh source, no compiled change. Until every install's packs compile
against a platform that ships this API, `ExportDeclaration.Resolve` carries the ONE transition
fallback (a suffix-aware `Deck`/`*/Deck` check). Deleting it is tracked in issue #1576.

### Export phase 2 (open, #1576)

The export templates (`ExportPdf.csx`, `ExportHtml.csx`) still branch on the suffix-aware type
check to pick slide-deck composition: they run from an export REQUEST that carries only the
source path, so they cannot read the source hub's configuration. Phase 2 puts the declared
`Composition` on the export request (stamped by `ExportDocumentLayoutArea`, which has the
declaration), after which the templates dispatch on the declaration and the type checks are
deleted.

## Import/serialize: design (phase 3, #1580)

The persistence parsers have the mirrored problem with a harder constraint: **parsing runs
before any node type activates** — import, bake and git-sync parse files in order to CREATE the
nodes whose types would carry a declaration. Today `MarkdownFileParser` hard-codes the one
special shape (slide files → `SlideContent`, Notes/Background frontmatter round-trip).

Decision: split the two directions, because only one of them has the bootstrapping problem.

- **Import (file → node) stays GENERIC, then re-materializes.** At parse time the node type's
  assembly may not exist, so the parser must not try to build typed content. It produces the
  generic shape — `MarkdownContent` plus ALL frontmatter fields preserved — and the owning
  hub's existing materialization pipeline re-types the content on first activation (the same
  mechanism that re-types an as-written `JsonObject` today; see the `ContentAs<T>` contract).
  The slide branch in `MarkdownFileParser` is then a re-materialization rule owned by the slide
  type, not a parser special case.
- **Serialize (node → file) dispatches on the CONTENT OBJECT, not the type name.** At export
  time the content instance is live, so a compiled public interface — `IFrontMatterRoundTrip`
  (name TBD): content record ⇄ frontmatter mapping — lets the parser serialize ANY type whose
  content implements it. In-mesh content records can implement a compiled interface (the
  platform assemblies are on the compile reference set), so a pack's content type carries its
  own file representation. No type-name gate on either side.

The bootstrapping edge this dodges deliberately: resolving declarations off the *installed
NodeType definition node* at parse time was rejected — it would execute configuration lambdas
(or partially interpret them) before activation, recreating the compile-order problem the
activation pipeline exists to solve.

Sequencing: phase 2 (request-carried composition) → phase 3 import split → delete
`MarkdownFileParser.IsSlideNodeType` and the template type checks. Both issues track the
deletions; the suffix-aware `Matches` predicates on `SlideNodeType`/`DeckNodeType` outlive them
only as long as any compiled reader still needs a type-shaped question answered.
