---
Name: Menus as Data
Category: Architecture
Description: Menu presentation — text, icon, order, grouping, visibility — is editable node content, so re-wording a menu entry is a node edit rather than a CI + image + rollout cycle. Applicability stays compiled.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="3" y1="6" x2="21" y2="6"/><line x1="3" y1="12" x2="15" y2="12"/><line x1="3" y1="18" x2="11" y2="18"/><circle cx="19" cy="15" r="3"/></svg>
---

Changing a menu label used to cost a full release: edit a provider (or `strings.en.json`, which is
**compiled into the hub assembly**), wait ~20 min for CI, ~18 min for the image publish, then a
rollout. Half an hour to rename a button — which is why the node menu accumulated entries nobody
pruned and wording nobody fixed.

**Menu presentation is now data.** A [`MenuPresentation`](/Doc/Architecture/MenuAsData) node per menu
context carries the text, icon, order, grouping and visibility of each entry. Editing it is a node
edit: it takes effect on the next render, for every renderer, with no build.

## The split that makes this safe

> 🚨 **Presentation is data. Applicability is code.** Whether an entry *may appear at all* — the
> viewer's permission, the node's type, whether sync is configured, whether this is a protected
> partition root — stays in the compiled providers. The catalog only re-dresses or removes what a
> provider already decided to show.

This is not a simplification, it is the security boundary. Node-menu permission filtering is enforced
by providers **not emitting** an item: `NodeMenuItemDefinition.RequiredPermission` is carried for
display and equality, but nothing filters on it (the *settings* menu does filter on it — the node
menu does not). An entry conjured from data would therefore have **no permission gate at all**. So
the catalog is deliberately **override-only**: it can re-word, re-icon, re-order, group and hide, and
it can never introduce an entry. A catalog edit cannot widen access, by construction.

| Concern | Lives in | Why |
|---|---|---|
| Label, tooltip (per locale) | **Data** | The thing people actually iterate on |
| Icon, order, grouping / sub-menu nesting | **Data** | "The menu is messy" is a layout problem |
| Hiding an entry | **Data** | Subtractive — cannot widen access |
| Permission gate | Code | Enforced by non-emission; the security decision |
| Node-type gate (Markdown / Deck / Space / User) | Code | Reads live node content |
| Sync configured, plugin installed, credential present | Code | Reads live hub state and services |
| Protected partition root, viewer's own home | Code | Reads identity and guard state |

That last group is the honest answer to "can it all be data?" — **no**, and it should not be. Those
predicates read live streams, and expressing them as data would mean inventing a predicate
mini-language whose failure modes are worse than the problem it solves. They stay compiled, they stay
CI-tested, and they are a *named handful* rather than an open-ended escape hatch.

### Why data rather than plugin code

Moving menu definitions into plugin C# stored in the mesh would also give fast iteration, but in-mesh
source is **never compiled by CI** — it compiles at runtime in the portal. A framework rename would
silently break it, and a NodeType left at `CompileError` can hold a new pod out of readiness. The
node menu is core navigation; the failure mode "the menu does not compile" is unacceptable where the
failure mode "one entry keeps its compiled label" is merely untidy. Data has no compile step, so that
state cannot exist.

## The shape

One node per menu context at `Admin/Menu/{Context}` — `Admin/Menu/Node`, `Admin/Menu/Mesh`,
`Admin/Menu/AI`, `Admin/Menu/GitHub`. It lives on the Admin partition because editing it changes
navigation for **every** viewer: that is a platform-admin act, not a per-space one.

```json
{
  "$type": "MenuPresentation",
  "context": "Node",
  "entries": [
    { "area": "Delete", "order": 90, "icon": "🗑️" },
    { "area": "ExportPdf",  "parent": "ExportDocument",
      "labels": { "en": "PDF", "de": "PDF" } },
    { "area": "ExportDocx", "parent": "ExportDocument",
      "labels": { "en": "DOCX", "de": "DOCX" } },
    { "area": "StopSync", "hidden": true }
  ]
}
```

Entries are matched to menu items by **`area`** — the stable identity every `NodeMenuItemDefinition`
already carries (it is the navigation target, so it cannot drift without the navigation drifting
too). Every field is optional and `null` means *leave the compiled value alone*, so an entry is a
patch rather than a replacement — an entry that sets nothing is a no-op, not a way to blank a label.

| Field | Effect |
|---|---|
| `area` | Match key (case-insensitive). Required. |
| `labels` | Locale tag → text. `de-CH` → `de` → `en` → the compiled label. |
| `tooltips` | Locale tag → hover text. Same fallback chain. |
| `icon` | Replacement emoji or SVG URL. |
| `order` | Replacement sort position — the menu is re-sorted after the overlay. |
| `hidden` | Drops the entry. |
| `parent` | `area` of the entry this nests under, rendered as a hover sub-menu. |

### Per-locale text is the point

`labels` is why a label change needs no build. A menu label today lives in `LabelKey` →
`strings.{en,de}.json`, and those catalogs are embedded resources in the hub assembly — so
re-wording an entry is a code change. Carrying the text per locale on the entry moves that edit into
data, and it closes a real gap: an entry with **no** `LabelKey` (the Deck export items, for one) is
English for every viewer today, and a catalog entry can translate it without touching the provider.

## Changing a label, end to end

1. Open `Admin/Menu/Node` in the portal (or `patch` it over MCP).
2. Add or edit the entry for the area — e.g. `{ "area": "Delete", "labels": { "de": "Löschen" } }`.
3. Save.

The catalog is a live stream combined into the menu render, so the next render picks it up — the same
path a permission change takes. **Seconds, no build, no image, no rollout**, and it reaches Blazor,
React, React Native and the native shell at once because all four read the same `$Menu:{context}`
slot.

## Fail-safe: a bad edit cannot cost you the menu

The catalog is an **overlay over the compiled items**, so every failure reduces to "no override
applied" — and every dropped input is *named* in the log rather than silently swallowed:

| What went wrong | What the viewer sees | What the log says |
|---|---|---|
| No catalog node at all | The compiled menu | nothing (this is the normal state) |
| Catalog node deleted | The compiled menu | nothing — absence is indistinguishable from never-created, by design |
| Content unreadable / wrong `$type` | The compiled menu | the path and the raw JSON, at Error |
| Entry with no `area` | The compiled menu | `entry #N … has no Area` |
| Two entries for one `area` | The first entry wins | `entry #N … repeats Area 'X'` |
| `parent` naming an unknown area | Entry stays **top-level** | `names Parent 'X', which no visible menu item provides` |
| `parent` pointing at itself | Entry stays top-level | `names itself as Parent` |
| A label that is blank in every locale | The compiled label, still translated | nothing — an unusable override is simply not applied |

There is no state in which a catalog edit produces an empty menu, because the catalog never *sources*
the menu — it only dresses it.

**The catalog is authored once and never reconciled.** Nothing rewrites it on boot. That is
deliberate: the last time menu/access shape was materialised per instance and reconciled by a
background pass, the churn drove one node to version 254,760 and left writer and reader disagreeing
about what the fields meant. A catalog absent an entry for some area is the *normal* state, not drift
to be repaired — which is also why a framework version that adds a new menu entry needs no migration:
the new entry simply renders with its compiled presentation until someone chooses to override it.

## Where it plugs in

The overlay runs in `RenderMenus` (`NodeMenuItemsExtensions`), the single point where every provider's
items are already merged, sorted and translated:

```
providers → CombineLatest → sorted set
                 │
                 ├── ⨯ MenuPresentation stream   ← the catalog node, live
                 │      patch · hide · nest · re-sort
                 │
                 └── Localized(access) → MenuControl → $Menu:{context}
```

Order matters: the overlay runs **before** `Localized(access)` and clears `LabelKey` wherever it
supplies a label, so the translation pass cannot resolve an override back to the compiled text. And it
**re-sorts**, because the providers sorted the compiled order before any override existed.

## See Also

- [Node Menu Items](/Doc/GUI/NodeMenu) — the provider contract, contexts, and how the menu renders
- [Localization](/Doc/Architecture/Localization) — the key catalog and `[Translation]`
- [Access Control](/Doc/Architecture/AccessControl) — permissions and the Admin partition
- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — why in-mesh code is a different risk class
