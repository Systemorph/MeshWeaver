---
Name: Hierarchy Diagrams
Category: Documentation
Description: HierarchyControl — a top-down tree of linked boxes (title and subtitle), the current node emphasised, built from flat nodes that name their parent.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="2" width="6" height="5" rx="1"/><rect x="2" y="17" width="6" height="5" rx="1"/><rect x="16" y="17" width="6" height="5" rx="1"/><path d="M12 7v5M5 17v-5h14v5"/></svg>
---

`HierarchyControl` draws a **structure** top-down: a whole and its parts, each part a box with a
title and a subtitle, joined to its parent by an elbow connector. A deal → its options → what each
option covers; a programme → its layers; an organisation → its units. Never draw this as hand-built
SVG in `Controls.Html` — compose the control.

```csharp
Controls.Hierarchy(
    new HierarchyNode("deal", null, "MTPL 2026", "Submission · 2 options", "Acme/MTPL2026", HierarchyEmphasis.Strong),
    new HierarchyNode("o1", "deal", "Layer 1", "7m xs 3m · 7.8% RoL", "Acme/MTPL2026/Layer1", HierarchyEmphasis.Current),
    new HierarchyNode("o2", "deal", "Layer 2", "15m xs 10m · 3.1% RoL", "Acme/MTPL2026/Layer2"),
    new HierarchyNode("s1", "o1", "Motor", "Section", "Acme/Sections/Motor"),
    new HierarchyNode("s2", "o1", "Liability", "Section", "Acme/Sections/Liability", HierarchyEmphasis.Muted))
```

# Nodes are FLAT

Each `HierarchyNode(Id, Parent, Title, Subtitle?, Href?, Emphasis?)` names its parent by id — the
shape a projection produces naturally, and one that crosses the wire without recursion. The tree
is resolved by the pure, unit-tested `HierarchyControl.Layout(nodes)` (`FigureControlsTest`):

- a node whose parent is blank, itself, or not among the nodes is a **root**;
- children keep their **input order**;
- a repeated id keeps its **first** occurrence;
- a **cycle** is broken at its first node in input order, so every node is drawn exactly once and
  no renderer recurses forever.

It returns `null` for no nodes, and the view says *"No structure to show."* in the viewer's language.

# A deal of options and layers, and chained covers

Beyond the six positional fields a node carries init-only slots for the shapes deals actually take:

| Slot | Vocabulary | Use |
|---|---|---|
| `Kind` | `HierarchyNodeKind`: `group`, `option`, `proportional`, `excess-of-loss`, `section` (open) | styles the box — a quota share and an XL layer must not look alike |
| `Relation` | free text, localized | the edge label to the parent — "option", "inures to", "on retention of" |
| `Arrange` | `HierarchyArrangement`: `row` (default), `stack` (open) | how the node's CHILDREN lay out; `stack` draws them one above the other, a tower (give the highest layer first) |
| `Badges` | short localized strings | status chips — a verdict, "bound", "★ favourite" |
| `Figures` | `KpiItem(Label, Value)` | small figures on the box — our line, our premium |

So the general deal — a submission → 2–5 mutually exclusive options (one `favourite`) → each
option's 1–6 layers → what each layer covers — stays readable: the options sit side by side, each
option `stack`s its layers into a column, and a layer's sections sit under it.

```csharp
Controls.Hierarchy(
    new HierarchyNode("qs", null, "Quota share 50 %", "30 % commission")
        { Kind = HierarchyNodeKind.Proportional, Arrange = HierarchyArrangement.Stack },
    new HierarchyNode("xl2", "qs", "XL layer 2", "10m xs 10m · 2.4 % RoL")
        { Kind = HierarchyNodeKind.ExcessOfLoss, Relation = "on retention of" },
    new HierarchyNode("xl1", "qs", "XL layer 1", "5m xs 5m · 6.1 % RoL")
        { Kind = HierarchyNodeKind.ExcessOfLoss, Relation = "on retention of",
          Figures = [new KpiItem("Our premium", "77.5k")], Badges = ["bound"] })
```

# Emphasis and links

`Emphasis` is an open vocabulary (`HierarchyEmphasis`): `current` — the node the page is about,
outlined in the accent colour and **never a link**; `strong` — the heading node, filled in the
accent colour; `muted` — a node of secondary weight; `favourite` — the preferred alternative, marked with a star; empty — an ordinary box. Every other node with
an `Href` is a link; `HierarchyControl.LinkOf(node)` decides. It normalises a bare mesh path to a
site-root-relative one and accepts absolute `http`/`https` URLs. Any other scheme (`javascript:`,
`data:`, …), a protocol-relative `//host`, and any href carrying a backslash or a control character
(browsers normalise both into `//host`) draw the box unlinked, because node content is authored
by people and a bound href must never become script. The favourite's star carries a localized
accessible name, so it is not conveyed by colour and glyph alone.

# Binding

`Nodes` is `object`: hand the rows over directly, or bind them to a live data section with a
`JsonPointerReference` ([Data Binding](/Doc/GUI/DataBinding)). `AnalysisRows.Resolve<HierarchyNode>`
reads both forms. The Blazor renderer ships in the analysis view pack (`MeshWeaver.Blazor.Analysis`,
`AddAnalysisViews`) and is drawn with CSS boxes and connectors, so it follows the theme and every
label stays selectable text.
