---
Name: Package Mark Inheritance
Category: Architecture
Description: How a page with no icon of its own wears its package's mark — where the partition-root step sits in the icon chain, why the resolver stayed synchronous and total, and why inheritance is opt-in per surface rather than global.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="8" height="8" rx="2"/><path d="M15 5h5"/><path d="M15 9h3"/><path d="M7 11v6a2 2 0 0 0 2 2h3"/><rect x="13" y="15" width="8" height="6" rx="2"/></svg>
---

# Package Mark Inheritance

A node with **no icon of its own** resolves its **partition root's** mark before falling through to
the generic glyph for its type. A lesson under `AgenticEngineering`, a game under `Chess`, a doc
under a store package — each one wore the same `document` chrome. Each one now wears its package's
mark on its page.

The package roots already carry those marks, and the store's were deliberately aligned to one visual
language — a bold 24×24 plate with white detail — *precisely so they read at 16 px* (MeshWeaver.Plugins
#588).

🚨 **The BROWSER TAB is the surface this was reported from, and it is the surface this change does not
finish.** The resolution is in place and `ResolveIconLink` accepts a root, but both things that
render a tab icon live in MeshWeaver.Plugins. See *What this change does NOT cover* below.

## The chain

`MeshNodeImageHelper.ResolveNodeIcon` (`src/MeshWeaver.Graph/`), with the new step in bold:

1. the node's **own** `Icon` — a `content:` reference resolved through the access-controlled content
   route, a URL, inline `<svg>`, an emoji
2. a **shipped glyph** of that name, when the icon is a Fluent icon NAME this assembly ships
3. **the partition root's own mark** — the same two resolutions applied to the root's `Icon`
4. the **NodeType** default (`/static/NodeTypeIcons/document.svg`, `code.svg`, `chat.svg`, …)
5. the **neutral box**

Steps 1–2 and 4–5 are untouched, and that ordering carries the whole policy:

- **A page's own icon always wins.** Marking a package can never overwrite what a page chose for
  itself.
- **An unmarked package changes nothing.** Step 3 yields null and the chain falls through exactly as
  it did, so adding the step cannot regress a package that never had a mark.
- **The chain is still total.** Every node resolves to something, so no card, avatar or tab can fall
  back to a bare initial. That guarantee predates this change and survives it.

### Only the root's OWN mark is inherited

Step 3 reads the root's `Icon` — it does **not** run the root's own chain. Falling through to the
root's NodeType default would dress every document in an unmarked `Space` as an *organization*,
which is strictly worse than the document glyph it replaced. A root with no mark contributes
nothing; the child keeps its own type.

### "Partition root" is the FIRST segment, and it never inherits

`PartitionRootPath` returns the first path segment: `Doc/Architecture/LinkPreviews` inherits from
`Doc`, not from `Doc/Architecture`. It returns **null** for a single-segment path — so a partition
root has no ancestor to inherit from and cannot resolve itself. That is structural, not a guard bolted
on afterwards.

The supplied root is also **verified, not trusted**: it is used only when its path really is the
node's first segment. A caller that hands over the wrong node — the parent instead of the root, a
stale frame from another page — gets no inheritance rather than an unrelated package's mark on
someone else's page. That is the failure mode a screenshot could not catch.

## The design decision: the resolver stays pure

`ResolveNodeIcon` is `static`, synchronous, and takes one `MeshNode`. The partition root is a
*different node*, and reading one is an `IObservable` — a shape this signature cannot express and its
callers are not built for. Two options:

| | make the resolver reactive | pass the already-resolved root in |
|---|---|---|
| resolver | returns `IObservable<string?>`; every caller becomes reactive | unchanged: pure, total, unit-testable with no mesh |
| call sites | all of them change, including Blazor render paths that cannot await | only the ones that opt in |
| a caller with no root | must still open a stream to get an answer | calls the one-argument overload, exactly as today |

**The second was taken.** An *overload* — `ResolveNodeIcon(node, partitionRoot)` — leaves the pure
resolver pure and leaves every existing call site compiling and behaving identically. Pushing
`IObservable` into a rendering helper would have made the resolution reactive in dozens of places that
have a node in hand and nothing to await on.

🚨 **An overload, not a fourth optional parameter.** `MeshNodeLayoutAreas.BuildHeader` is a
module-facing contract, and adding a parameter — default or not — *replaces* the signature every
already-compiled module was built against. That is the same binary-contract argument
[`PageIcon`](../ContentFaviconRasterization) makes for using `init` properties instead of primary
constructor parameters, and `scripts/check-record-signatures.py` is the gate that states it.

## The reactive half

`MeshNodeExtensions.ObservePartitionRoot(workspace, nodePath)` is the seam that fetches what the
resolver cannot fetch for itself:

```csharp
host.Workspace.GetMeshNodeStream().CombineLatest(
        host.Hub.GetEffectivePermissions(hubPath),
        host.Workspace.ObservePartitionRoot(host.Hub.Address.Path),
        (node, permissions, partitionRoot) => …)
```

Four properties are load-bearing:

- **A point read that is legitimate.** Reading one node by exact path is only correct for a path
  known to exist — a point read of an absent node answers a routing NotFound that terminates the
  stream *and* opens the storm-breaker on that path (see
  [CQRS and Content Access](../CqrsAndContentAccess)). A partition root exists for every node that
  has one, by construction: it is the namespace the node lives in.
- **A root opens no read at all.** When there is no distinct partition root the stream is a constant
  that completes — no subscription, no path to storm.
- **It starts null.** `CombineLatest` produces nothing until *every* source has emitted, so without a
  seeded first value the root read would gate the page — and a slow or refused root read would leave
  it blank. Seeding null means the page renders on the node's own stream immediately.

  🚨 **That is visible, and it is the accepted trade.** The header paints its type glyph on the first
  frame and re-renders with the package's mark when the root arrives. The alternative — waiting for
  the root before the first paint — trades a brief icon swap for a page that can hang on a read it
  does not need. `PartitionRootStreamTest` **waits for the mark rather than reading the first frame**
  for exactly this reason; asserting on frame one measured the un-inherited render and reported the
  wiring as broken, which is what it did on its first run.
- **`DistinctUntilChanged` on `(Path, Icon)`.** The root node emits on every touch of it —
  `LastModified`, a content edit, a new child. Without this, every one of those would re-render every
  page in the partition for an icon that did not change.

🚨 **One fault is a STATE, not an error.** Access to a node does not imply access to its partition
root: an `AccessAssignment` can share a single node out of a partition the viewer is not a member of.
A denial there means "this viewer inherits nothing", and it is classified by
`AreaErrorClassifier.IsExpectedUserActionFailure` — the same predicate
`MeshNodeThumbnailControl.ShouldSurfaceStreamError` already uses for the same reason. **Nothing else
is caught.** A genuine infrastructure fault propagates to the page, because a decoration quietly
swallowing infrastructure faults is how a broken mesh renders as a working one.

## Inheritance is opt-in per SURFACE

Every call site kept compiling; the ones that pass a root were chosen, not swept.

**A page identifies ONE node**, so the package mark is pure gain there. The node-page header
(`MeshNodeLayoutAreas.Overview`, `ContentData`) and the markdown page header
(`MarkdownOverviewLayoutArea.Overview`) pass the root.

**A LIST of siblings is not one node.** In a mixed child listing the NodeType glyph is what tells a
doc from a code node from a thread; flattening a rail to the same package mark repeated twelve times
would take that away and give nothing back. So the nav rail's child links, cards, pickers and search
results keep resolving through the one-argument overload.

That split is a policy, and it is the reason the mechanism is an overload rather than a global change
to the chain: a surface that wants inheritance asks for it.

## What this change does NOT cover

- **The crawler-facing head.** `SeoResolver.ResolveIcon` deliberately resolves *the node's own mark or
  nothing* — no NodeType stand-in, no synthesis — and it is a different chain from
  `ResolveNodeIcon`. Extending it to inherit a package mark is a defensible next step and a genuinely
  separate policy question; its consumer (`Memex.Portal.Gui/Seo/SeoHead.razor`) also lives in
  MeshWeaver.Plugins, so it is a two-repo change. See [Link Previews](../LinkPreviews).
- **The in-circuit tab.** `MeshNodeImageHelper.ResolveIconLink` now takes an optional root and
  inherits when given one, but its caller — `MeshWeaver.Blazor/Pages/ApplicationPage.razor.cs` — is in
  MeshWeaver.Plugins. Until that opts in, a signed-in tab keeps the NodeType glyph. The same
  cross-repo seam [Content Favicon Rasterization](../ContentFaviconRasterization) describes.

An inherited mark that is an **official third-party mark** still yields the portal's own mark in the
tab: a favicon claims the tab *is* the site's, and that claim does not become truer by being
inherited.

## Verifying it

The pure chain, with no mesh, is pinned by `PackageRootIconInheritanceTest`; the reactive seam is
pinned against a real mesh by `PartitionRootStreamTest` (both in `test/MeshWeaver.Graph.Test`). In a
running portal, open a page under a marked package and read the header tile — a doc under `Chess`
shows the board, not the document glyph, and `Chess` itself is unchanged.
