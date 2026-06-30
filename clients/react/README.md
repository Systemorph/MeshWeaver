# @meshweaver/react

A React + **Fluent UI** renderer for MeshWeaver layout areas — the web / Electron / React-Native-capable
counterpart of the MAUI `MauiViewPack`. It walks the **same `UiControl` JSON tree** the Blazor portal and
MAUI render, mapping each control to a Fluent UI React v9 component (the Blazor portal renders with Fluent
UI Blazor, so the mapping is near 1:1).

## Why this shape

```
        layout-area stream  ({areas, data} UiControl tree + RFC-7396 patches)
                    │
          ┌─────────┴─────────┐
          │  renderer core    │   dispatch on $type, pop skins, resolve bindings, post events
          │ (transport-free)  │   ← written ONCE
          └─────────┬─────────┘
       DOM leaf pack        RN leaf pack
   web / Electron        iOS / Android
```

The renderer depends only on an **`AreaSource`** (the `{areas,data}` tree + an event sink), so it's
transport-agnostic. Feed it the in-memory demo source, or a gRPC-backed live source built on
`@meshweaver/client`. The control→component mapping is one Fluent leaf pack; React Native swaps the leaf
pack, the dispatch/binding/skin core is unchanged — exactly how MAUI has a native pack and Blazor a web pack.

## Run the demo (see it)

```bash
npm install
npm run dev        # Vite dev server → a rich sample area rendered with Fluent UI
```

The demo renders ~50 controls (stacks, grids, cards, tabs, a data grid, a chart, data-bound form inputs,
nav, feedback) from a single `{areas,data}` tree. Edit a field — the binding writes back through its
`/data` pointer (optimistically applied, exactly as a live stream would echo the merge-patch). Click events
print at the bottom.

## Coverage

`controlRegistry` maps the full vocabulary: layout (`Stack`/`LayoutGrid`/`Tabs`/`Toolbar`/`Splitter` via
skins), display (`Label`/`Markdown`/`Html`/`Badge`/`Icon`/`CodeSample`/`Exception`), data
(`DataGrid` + `PropertyColumn`/`TemplateColumn`, `Catalog`, `Chart`), inputs
(`TextField`/`TextArea`/`NumberField`/`CheckBox`/`Switch`/`Slider`/`Date`/`DateTime`/`Select`/`Combobox`/`Listbox`/`RadioGroup`/`Button`/`MenuItem`/`SearchBox`),
navigation (`NavMenu`/`NavGroup`/`NavLink`), feedback (`Progress`/`Spinner`), editors
(`CodeEditor`/`MarkdownEditor`/`DiffEditor` — textarea-based; swap in Monaco for full parity), and the
mesh controls. Unknown `$type`s render a clearly-labeled fallback; extend or override by spreading into
`controlRegistry`.

## Wiring to a live mesh

Implement `AreaSource` over `@meshweaver/client`'s `watch` (subscribe to `@addr/area/id`, fold the
RFC-7396 patches into `{areas,data}`, route `emit` to `ClickedEvent`/`UpdatePointer`). The layout-area
subscription is the one `WIRE:`-level piece still to validate against a running portal — once pinned, the
same renderer drives web, Electron, and React Native.

## Targets

- **Web / Vite / Next.js** — the demo as-is.
- **Electron** — wrap the built web app in a BrowserWindow (desktop).
- **React Native / Expo (iOS, the MAUI peer)** — reuse `area/` + `render/` (dispatch, binding, skins) and
  provide an RN leaf pack (`<View>`/`<Text>`/RN inputs) instead of the Fluent DOM components.
