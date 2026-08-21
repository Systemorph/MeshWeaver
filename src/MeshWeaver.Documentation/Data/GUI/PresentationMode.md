---
Name: Presentation Mode — the per-viewer privacy screen
Category: Documentation
Description: A display-only, per-user screen that keeps marked paths off tile, card and completion surfaces while a viewer is sharing their screen — never a permission, never global
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="15" r="4"/><circle cx="18" cy="15" r="4"/><path d="M14 15a2 2 0 0 0-2-2 2 2 0 0 0-2 2"/><path d="M2.5 13 5 7c.7-1.3 1.4-2 3-2"/><path d="M21.5 13 19 7c-.7-1.3-1.5-2-3-2"/></svg>
---

The portal's home reveals everything the signed-in viewer can read: the catalog lists every space,
"last accessed" names whatever they touched this morning, the Pinned tab sits right there, link-preview
cards carry names and logos, and `@` completions offer node names as you type. Share that screen
with an external audience and you have shared your engagement list.

**Presentation mode** is the privacy screen for that moment: a per-viewer, display-only mode that
keeps marked paths off those surfaces while it is on.

> 🚨 **It is not access control, and it must never become access control.** It grants nothing and
> denies nothing. A marked node stays readable, stays reachable by its URL, stays in the viewer's
> own search results, and is completely unchanged for every other user. The moment a screen gates a
> read it is a *second* permission system that can disagree with the real one — and the one that
> disagrees quietly is the one that ships a bug nobody can see.

---

# The seam

```
User.PresentationMode  +  User.HiddenPaths      (the VIEWER's own profile — never the node)
  └─► PresentationScreen                        (pure: Active + MarkedPaths, one predicate Hides(path))
        └─► screen.Filter(items, pathOf)        (applied where a surface PAINTS, never to a query)
```

`PresentationScreenExtensions.ViewerScreen(hub)` is the one resolver. It reads WHO is asking exactly
the way [DisplayTimes](../DisplayTimes) reads the viewer's zone — off the live `AccessContext`,
request-scoped first, then per-circuit — and then binds the value to the viewer's own `User` node
through the shared node-stream handle.

**Why the identity comes from the context but the value does not.** A time zone changes about never,
so projecting it onto the `AccessContext` when the circuit opens is always right. A presentation
toggle is flipped in the *seconds before* a screen share, and a context snapshot taken at circuit
open would still say "off" — the viewer would toggle, watch the header light up, and share a portal
that is still listing everything. A stale zone is a cosmetic lag; a stale screen is the leak. So the
value is read live, and the toggle re-renders every bound surface with no reload.

---

# The two facts, and why a mark alone does nothing

| Fact | Where it lives | On its own |
|---|---|---|
| the mode is **on** | `User.PresentationMode` | nothing is hidden until something is marked |
| a path is **marked** | `User.HiddenPaths` | **hides nothing** while the mode is off |

That is what makes the feature reversible in one click: turn the mode off and every mark is inert,
with no restore step and nothing to clean up. It is also what the rejected workaround could not do —
renaming a node's display fields is *global*, so it hides the name from everybody and has to be
undone afterwards.

**Marking a space covers its subtree.** The path IS the name, so listing `Acme/Q3-Renewal` under
"last edited" would leak exactly what marking `Acme` was meant to keep off the screen. Containment
is by path segment, so `Acme` never hides `AcmeCorp`.

---

# Where the filter goes

**Filter where a surface PAINTS — never by narrowing a query.**

Three surfaces are the exception that proves it, and they filter *earlier*, before the query is
built. The home's **Pinned**, **Shared with me** and **Apps** tabs each interpolate the viewer's own
paths straight INTO their control's query string, which the search view exposes in its options
editor and carries in the `hq=` parameter of "open in search". A marked name reaching the address
bar mid-presentation is the leak, whether or not a card for it is ever drawn.

A tab the screen empties is **dropped**, not shown empty: a tab labelled "Pinned" with nothing under
it says something all by itself.

Everywhere else the query is left alone. A `-path:Acme` clause would put the marked name into that
same URL *and* would make the screen a query-engine concern — the first step towards it becoming the
second permission system above.

| Surface | Where the screen is applied |
|---|---|
| **Spaces** tab, search results, node catalogs, tree levels, graph navigator | `MeshSearchView` — one filter over the results every `MeshSearchControl` renders |
| **Pinned** tab | `UserActivityLayoutAreas.BuildPinnedItems` — before the query string is built |
| **Shared with me** tab | `UserActivityLayoutAreas.BuildHome` / `BuildCatalog` — same reason |
| **Apps** tab | `UserActivityLayoutAreas.BuildApps` — same reason. A `~/` entry is a system AREA, not a mesh path: it names no node, so there is nothing to screen and it passes through |
| OG / link-preview cards | `OgCardLayoutArea` — a screened target is dropped, not redacted |
| `@` completions | `ChatCompletionOrchestrator.Screened` (one seam for every producer) and `MeshNodeAutocompleteProvider` |

**A dropped card is dropped, not redacted.** A card whose title reads "hidden" still tells the room
that something is being hidden.

---

# Rules for a new surface

0. **Ask `HidesAnything`, never `== PresentationScreen.Off`.** They are different values: a viewer
   who marked things and then turned the mode off holds a screen that is not `Off` and yet hides
   nothing. `Filter` is a no-op for them either way — but anything ELSE your fast-path's other
   branch does is not, and that is how an empty completion category came to be suppressed for
   someone who was not presenting at all.
1. **Resolve the screen ONCE, on the render turn**, and pass it down as a value —
   `host.ViewerScreen()` in a layout area, `Access.ViewerScreen(hub)` elsewhere. Reading the ambient
   `AccessContext` from inside a later emission lands after a scheduler hop with the `AsyncLocal`
   gone: it resolves to "nobody", whose screen hides nothing. That is the silent-failure shape.
2. **Do not paint before the screen is known.** "Hidden items never appear or flash" is the
   requirement; a view that renders neutral and filters a beat later shows the audience exactly what
   the mode was turned on to hide. Gate the first paint on the first emission — an anonymous or
   system caller resolves synchronously, so the gate costs those views nothing.
3. **Never widen the screen on a fault.** The resolver holds the last known screen across a faulting
   profile stream (and logs it) rather than resetting to "nothing hidden" mid-presentation. Do not
   add a timeout that falls back to the neutral screen — that fails open.
4. **Never consult it for anything but rendering.** No read, no write, no route, no permission.

---

# Not yet covered

The screen is a *portal* feature and covers the Blazor surfaces above. Still open:

- the **React client's** `MeshSearch` renderer does not apply it yet;
- the **strict variant** #1803 mentions — hiding all recent-activity regions wholesale, rather than
  the marked paths — is not built;
- there is **no keyboard shortcut** for the toggle; it is the user-menu entry and the header
  indicator.
