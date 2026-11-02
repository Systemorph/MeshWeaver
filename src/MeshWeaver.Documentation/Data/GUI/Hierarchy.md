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

# Emphasis and links

`Emphasis` is an open vocabulary (`HierarchyEmphasis`): `current` — the node the page is about,
outlined in the accent colour and **never a link**; `strong` — the heading node, filled in the
accent colour; `muted` — a node of secondary weight; empty — an ordinary box. Every other node with
an `Href` is a link; `HierarchyControl.LinkOf(node)` decides, and normalises a bare mesh path to a
site-root-relative one exactly as `TowerControl.NavigableHref` does.

# Binding

`Nodes` is `object`: hand the rows over directly, or bind them to a live data section with a
`JsonPointerReference` ([Data Binding](/Doc/GUI/DataBinding)). `AnalysisRows.Resolve<HierarchyNode>`
reads both forms. The Blazor renderer ships in the analysis view pack (`MeshWeaver.Blazor.Analysis`,
`AddAnalysisViews`) and is drawn with CSS boxes and connectors, so it follows the theme and every
label stays selectable text.
