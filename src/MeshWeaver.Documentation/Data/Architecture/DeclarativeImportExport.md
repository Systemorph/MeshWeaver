# Declarative export and import

How a node type tells the platform what its content can DO — export to PDF, round-trip through
a git file — without the platform carrying a compiled `if (nodeType == …)` for it. The goal is
composition-first: a plugin pack declares; core mechanisms read declarations.

## Export: SHIPPED — no compiled type gate survives (#1576)

A node type declares its export behaviour on its own hub configuration:

```csharp
config.WithExport(ExportDeclaration.Document)    // PDF / Email / DOCX over the node's body
config.WithExport(ExportDeclaration.SlideDeck)   // PDF / Email, one page per slide
config.WithExport(new ExportDeclaration { Formats = ExportFormats.Pdf })
```

`ExportDeclaration` (MeshWeaver.Graph) rides the same `MessageHubConfiguration.Set` idiom as
`PageLayoutOptions`. Readers:

- **`ExportMenuProvider`** — THE export node-menu provider, one for all types. It reads
  `Configuration.Get<ExportDeclaration>()` off the focused node's hub and renders the Export group
  with exactly the declared entries. The former per-type providers (`MarkdownExportMenuProvider`,
  `DeckExportMenuProvider`) are deleted; a new exportable type declares instead of adding
  compiled code.
- **`ExportDocumentLayoutArea`** — offers the pixel-fidelity picker when the declared
  `Composition` is `SlideDeck`.
- **`ExportPdf.csx` / `ExportHtml.csx`** — dispatch on the declared `Composition`.

Because the declaration travels on the hub configuration, a PLUGIN type declares in its own
configuration lambda — in-mesh source, no compiled change.

### How the declaration reaches a template

A template cannot read a hub configuration: it runs from an `ExecuteScriptRequest` carrying only
the source path. **`ExportDocumentHandler` runs ON the source node's own hub** — every caller
targets `new Address(sourcePath)` — so it reads the type's declaration there and stamps
`composition` into the script inputs. The templates dispatch on that input. Same declaration,
carried to where it is needed; no template ever asks what the node type is called.

### The transition fallback is GONE

`ExportDeclaration.Resolve(configuration, nodeType)` briefly answered `SlideDeck` for any type
whose name ended in `/Deck`, bridging plugin decks whose in-mesh lambdas predated `WithExport`.
It is deleted: `Publish/Deck` declares (MeshWeaver.Plugins#553), and every reader now asks the
configuration directly.

🚨 **Do not reintroduce a name gate as a "safety net".** Its failure mode is not an error — a type
it does not recognise composes as a plain document, and a deck-shaped node has no body of its own,
so the export SUCCEEDS and produces an empty file. Green activity, nothing logged, nothing in the
document. `DeclaredCompositionExportTest` (MeshWeaver.Plugins) pins it with a declared type that no
suffix check can match. A type that declares nothing exports nothing — the honest answer.

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

### 🚨 What blocks the import half — measured, not assumed

The tempting shortcut is to make the import rule a CONTENT-SHAPE rule instead of a type rule:
*"frontmatter carries `Notes`/`Background` ⇒ build the slide-shaped content"*. It is symmetric,
needs no registry and no bootstrapping — **and it silently empties real slides.**

Counted on the education repo's live slide corpus (2026-08-21):

```
slide files: 235 ; WITHOUT Notes/Background: 3
```

Those three carry neither key, so a shape rule imports them as `MarkdownContent`. The slide views
read `SlideContent`, and `ContentAs<T>` recovers only a SAME-short-named type — so the stage
renders empty, with nothing logged. Three silently broken slides is the same failure class this
whole programme exists to remove, so the shape rule is refused.

That leaves the registry route: resolve the node type's content CLR type at parse time through
`IMeshContentTypeRegistry.TryResolveByNodeType` (a dictionary lookup — it executes no
configuration lambda, so it does NOT recreate the compile-order problem the design rejected). Two
things must be settled before that is safe, and both are about what happens when the lookup MISSES
or resolves to a type that cannot carry the body.

**First, the actual exposure**, so neither condition is argued in the abstract. Every markdown node
file across the four plugin repos (MeshWeaver.Plugins, Education, Reinsurance, SocialMedia), keyed
on real front matter rather than on fenced examples in prose:

| files | `NodeType` | declared content type | carries the body? |
|---:|---|---|---|
| 621 | `Markdown` (599 implicit + 22 explicit) | `MarkdownContent` | ✅ the format's native shape |
| 506 | `Edu/Lesson` | **`MarkdownContent`** | ✅ native |
| 396 | `Edu/Exercise` | **`MarkdownContent`** | ✅ native |
| 232 | `Publish/Slide` | `SlideContent` | ✅ `Content` — this IS the case to convert |
| 79 | *(no front matter)* | `MarkdownContent` | ✅ native |
| 27 | `Agent` | *a different parser* — `AgentFileParser` claims `nodeType: Agent` and nothing else | n/a |
| **12** | **`Edu/Module`** | **`ModuleContent` — `Summary` only** | ❌ **no `content` member** |
| **11** | **`Skill`** | **`SkillDefinition` — its body member is `Instructions`** | ❌ **no `content` member** |

So the whole risk surface for condition 2 is **twenty-three files**, not an unbounded set: the two
big non-native types turn out to declare the native `MarkdownContent` anyway.

⚠️ Note `Skill` is NOT claimed by `AgentFileParser` — that parser returns null for anything whose
front matter is not `nodeType: Agent` — so a markdown-authored skill goes through
`MarkdownFileParser` like any other document. Its content type's body member is `Instructions`,
which puts it in the same bucket as `Edu/Module` for this analysis. (Whether a `.md`-authored skill
round-trips correctly TODAY is a separate question this note does not answer; most skills in the
repos are authored as `.json`.)

1. **A miss must not lose the extra frontmatter.** A cold-boot import (bake, first git-sync) can
   run before the type registers. Falling back to `MarkdownContent` drops `Notes`/`Background`
   permanently, so the fallback has to preserve them — which means writing content with no `$type`
   and letting the read seam re-materialize it (`TryRecoverForNodeType`, whose
   `DiscriminatorAdmits` guard refuses content whose own `$type` contradicts the node type — an
   absent discriminator is admitted, which is exactly why the fallback must omit it).
   🚨 **This is the stubborn one.** It closes the *extras* hole but not the empty one: a slide with
   NEITHER key has no extras to preserve, so a miss still yields `MarkdownContent` and an empty
   stage. Same three files as above. A miss has to be made impossible (prove the type is registered
   before any `.md` of it is parsed) rather than merely lossless.
2. **A hit must not drop the body.** Deserializing `{content, …extras}` into a record with no
   `content` member loses the markdown for every node of that type. This one is closable **by
   construction** rather than by audit: use the resolved type only when it can actually carry the
   body (a reflection check for the member), else keep the native shape. `Edu/Module`'s twelve
   files — and `Skill`'s eleven — then land exactly as they do today. For `Edu/Module` that is also
   the correct outcome on its own terms: its page is H1 + Summary + `Theory/` children and never
   renders a body.

Sequencing: export phase 2 is **done**; phase 3 (the import split) still owns the deletion of
`MarkdownFileParser.IsSlideNodeType`. The suffix-aware `Matches` predicates on
`SlideNodeType`/`DeckNodeType` outlive it only as long as a compiled reader still needs a
type-shaped question answered — after phase 3 the parser is the last one, which is what would let
`SlideContent`/`SlideNodeType` leave the platform entirely (they stayed behind when #1589 phase 2
retired the built-in Slide/Deck node types, *because of* this parser and the export gates).
