---
Name: Node Page Provenance
Category: Architecture
Description: The Type · Created · Updated line rode on one renderer, so a node type that drew its own landing page dropped it silently — 86 of 99 pages across the fleet had lost it. What the census found, why the default was inverted rather than the call sites patched, how a page declines the line in a sentence, and the two halves of the problem core cannot reach.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="8" y1="13" x2="16" y2="13"/><line x1="8" y1="17" x2="13" y2="17"/></svg>
---

# Node Page Provenance

**The page a node lands on now carries "who wrote this, and when" by default. Declining it is a
sentence somebody wrote down, not an absence somebody has to interpret.**

The provenance line is the standard header's second row — `Type · Created · Updated`, built by
`MeshNodeLayoutAreas.BuildMetaRow` from the node's own `CreatedDate` / `CreatedBy` /
`LastModified` / `LastModifiedBy`, rendered in the viewer's time zone with every label localized.

## What went wrong

The line rode on ONE renderer: the framework's `Overview`, registered by
`AddDefaultLayoutAreas`, which composes it through `BuildDetailsContent → BuildHeader`. Any node
type that wanted a designed landing page registered its own renderer for that area — and
`LayoutDefinition.WithNamedRenderer` is last-writer-wins, so the framework's renderer went away and
the provenance line went with it.

Nothing recorded that. The state on the node was intact the whole time: `CustomerJourneys` carried
`createdBy: rsalzmann`, `createdDate: 2026-09-15T06:53:47Z`, `lastModifiedBy: rsalzmann`,
`lastModified: 2026-09-15T07:17:01Z`. Only the header omitted it.

The second-order problem is the one that mattered more. Some omissions were *weighed*: an
`Activity` page reports `Compilation · Succeeded · started 09/16 11:56 · ended —`, which answers the
question better than `Created`/`Updated` would; a `Thread` answers it per message, in the
transcript. Others were nobody's decision at all. **From outside, those are the same observation —
an absence.** A reviewer could not tell a considered page from a forgotten one, and neither could
the next author.

## The census, measured 2026-09-16

Landing pages that REPLACE the framework renderer, and what they do about provenance:

| Repository | Landing pages that replace the renderer | Carry the line | Do not |
|---|---:|---:|---:|
| MeshWeaver (`src/`) | 7 | 3 | **4** |
| MeshWeaver.Plugins (compiled + in-mesh) | 92 | 10 | **82** |
| **Total** | **99** | **13** | **86** |

Core's other 61 node types never replace it and were never at risk — they land on the shared
`Overview` and get the line for free. The four in core that had lost it were `PluginCatalog`,
`GlobalSettings`, `User` and `Partition`; the last of those is the quietest of the lot, because
`PartitionNodeType` mentions no page at all — it repoints the default area to `Search` with
`WithDefaultArea`, and the landing page moves off the framework renderer as a side effect.

The reported floor was "at least three". The real ratio is **87% of pages that opt out of the
framework renderer**, and most of them in a repository no core reviewer reads. That number is what
decided the shape of the fix: at 87%, patching call sites leaves the next page to be forgotten.

## The shape: the default carries it

`MeshNodeLayoutAreas.WithNodePage` replaces the bare `WithView` for a landing area, and its
NO-ARGUMENT form composes the provenance line above the page:

```csharp
// The framework composes the line above your page. This is what you write when you have no
// opinion about provenance — and it produces a correct page.
layout.WithNodePage(MeshNodeLayoutAreas.OverviewArea, MyPage)

// The page already draws the standard header (it calls BuildHeader). The framework adds nothing;
// two provenance lines on one page is a worse page than one.
layout.WithNodePage(OverviewArea, MyPage, NodePageProvenance.RenderedByThePage)

// The page carries none, deliberately. The reason is MANDATORY — Declined("") throws.
layout.WithNodePage(OverviewArea, MyPage, NodePageProvenance.Declined(
    "an Activity reports the run's own start and end, which is the question this page is asked"))
```

Three properties make this more than a rename of "opt-in" to "opt-out":

1. **Forgetting produces a correct page.** The failure mode changes from "the line silently
   disappears" to "the line silently appears", and the second is visible.
2. **A decline is a sentence.** `NodePageProvenance.Declined` refuses a blank reason, so the record
   cannot say "somebody decided" without saying what.
3. **The verdict cannot go stale.** It lives on the `LayoutDefinition` beside the renderer it
   describes, and `WithNamedRenderer` — which every `WithView` overload funnels through — REMOVES
   it. A node type taking over a landing page is exactly the move that loses the line; if the
   framework's own verdict survived that takeover, every check downstream would pass having
   measured a renderer that is no longer registered.

`LayoutDefinition.GetNodePageProvenance(area)` reads it back, and `null` — "nobody decided" — stays
distinguishable from `Declined` — "decided no".

The composed strip honours the same per-node opt-out the standard header does,
`ExcludeFromContext: [header]`, so a chrome-less marketing cover does not grow a metadata strip it
was explicitly built without.

## What core decided for its own four

| Node type | Verdict | Why |
|---|---|---|
| `PluginCatalog` | framework-supplied | a catalog node is declared, published and re-pointed by people; "who set this source up, and when" is a question its page is asked |
| `GlobalSettings` | framework-supplied | "who last changed the platform's settings" is the first thing an administrator on that page wants |
| `Partition` | framework-supplied | an administrative record whose first question is who declared the space, and when |
| `User` | **declined** | the subject is a PERSON, and the node's stamps describe the ROW: `LastModified` moves when a preference is flipped and `CreatedBy` is the sign-in flow, so "Updated … by system-security" across somebody's home states a database fact to a reader asking about a colleague. The real question — "member since" — belongs in the profile the page already renders. |

The three compile/refusal overlays (`NodeTypeEnrichmentHelpers`) also decline: they are not the
node's page but what stands in its place while the page cannot be built, and the node's own stamps
describe the INSTANCE while the broken thing is its TYPE.

## Measuring it on a rendered page

The strip renders into an area with a stable id, `MeshNodeLayoutAreas.NodeMetaArea` (`NodeMeta`).
Nested containers render into `{parentArea}/{childId}`, so its depth varies by page but its store
key always ends in `/NodeMeta`; on a page the framework composed it for, the key is exactly
`{area}/NodeMeta`.

That id exists because the previous way to answer "does this page carry provenance?" was to read
the rendered HTML for the English word `Created:` — an assertion that a German reader sees an
English page, and one that would keep passing after somebody translated it.

## What the guard covers, and the two halves it cannot

`NodePageProvenanceGuard` scans `src/` for a renderer registered on a landing area through the bare
`WithView` / `WithNamedRenderer`, and reds naming the file. Exceptions are reasoned lines in
`test/NodePagesWithoutProvenance.allow`, which shrinks only — a stale entry reds, so the reason
beside it is re-read when it stops being true.

It deliberately **over-flags**: an area name is a landing page on one hub and an ordinary tab on
another (`Search` is `Partition`'s landing page and every other node's search tab), and a scanner
cannot tell which hub a registration lands on. Over-flagging costs a reasoned line; under-flagging
costs a page nobody notices is missing something.

Two halves are outside its reach, and saying so is part of the design rather than an omission:

- **In-mesh source.** Most of the fleet's landing pages are `Source/*.cs` node content and NodeType
  `configuration` lambdas that compile at RUNTIME in the portal — invisible to every compiler and
  every scan in this repository. Those 82 pages are reached by the changed DEFAULT, not by the
  guard: a page that moves to `WithNodePage` gains the line, one that stays on `WithView` stays as
  it is. Widening the guard's roots would produce a guard that scans a tree the defect does not live
  in.
- **The composed page is not the only shape.** A page can render `BuildMetaRow` itself, anywhere in
  its own tree, and declare `RenderedByThePage`. The guard takes that declaration at its word; it
  does not verify the call. Verifying it would mean rendering every node type's landing page, which
  needs an instance of each type, and the declaration is the thing a reviewer reads anyway.

## Related

- [User Interface](../UserInterface) — how a layout area is composed
- [Localization](../Localization) — why the strip's labels are keys and its timestamps are zoned
- [Reading CI Signals](../ReadingCiSignals) — why a guard that cannot fail is not a guard
