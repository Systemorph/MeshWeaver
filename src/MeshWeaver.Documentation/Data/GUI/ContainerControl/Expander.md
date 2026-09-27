---
Name: Collapsible Sections with the Expander Skin
Category: Documentation
Description: Wrap any control in a collapsible section whose open/closed state the layout area DECLARES from the data, while the viewer can still toggle it — per viewer, never persisted
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18"/><path d="M7 6.5l1.5 1 1.5-1"/></svg>
---

`ExpanderSkin` turns any control into a **collapsible section**: a header row with a title (and,
optionally, a one-line summary) that the viewer clicks to show or hide the content. It is a skin,
not a new container, so it composes with everything — a [Stack](../Stack) of fields, a data grid,
a code view.

# Basic Usage

Add the skin **last**, so it is the outermost skin and wraps the whole control:

```csharp
Controls.Stack
    .WithView(Controls.Stack
        .WithView(Controls.Markdown("Requested by **alice** — roll `build` onto 3.0.0."), "Reason")
        .AddSkin(Skins.Expander("Request").WithSummary("roll of build · alice")), "Request")
    .WithView(Controls.Stack
        .WithView(Controls.Markdown("Every remaining field."), "Fields")
        .AddSkin(Skins.Expander("Details").WithSummary("12 fields").WithExpanded(false)), "Details")
```

| Member | Meaning |
|---|---|
| `Title` | The header text, always visible. |
| `Summary` | A one-line summary beside the title — what a reader needs from a *collapsed* section. |
| `Expanded` | Whether the section is **declared** open. `null` (the default) means open. |

# The declaration and the viewer's toggle

`Expanded` is a **declaration**, never state. Compute it from the data the area renders — the
typical use is a page whose most relevant section depends on the node's state:

```csharp
var running = run.State == RunState.Running;
// AddSkin returns a NEW control — the skinned one is what you place on the page.
var execution = section.AddSkin(Skins.Expander("Execution").WithExpanded(running));
```

The viewer's click toggles a local, per-viewer state that is **written nowhere** — not onto the
node, not into the area's data, so two viewers of one page never fight over it. The renderer keeps
the viewer's choice across re-renders that declare the **same** value (a clock tick, a sibling's
update), and re-applies the declaration when a later emission declares a **different** one: when the
run moves from *Running* to *Done*, the section the author now declares closed closes, whatever the
viewer had done before.

Never persist the toggle on a node to "remember" it. That turns a viewing preference into a write
every viewer races on, and it is exactly the replicate-then-save shape
[Data Binding](../../DataBinding) forbids.

# Rendering

The renderers live with the view packs in MeshWeaver.Plugins: Blazor renders the skin as a Fluent
accordion item, the React client as the same header, summary and chevron, and both honour the
declaration/toggle contract above. A portal whose image predates them has no view for the skin and
renders the control through the unknown-skin fallback, so check the portal's version before relying
on it.

# See Also

- [Container Control](../../ContainerControl) — overview of all container controls
- [Stack Control](../Stack) — the usual content of a section
- [Tabs](../Tabs) — when only one of several panels should be visible at a time
