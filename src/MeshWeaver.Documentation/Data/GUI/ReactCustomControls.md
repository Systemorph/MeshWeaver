---
Name: Custom React Controls
Category: Documentation
Description: Extend the React renderer (@meshweaver/react) with your own control — define a UiControl subclass server-side, register a React component for its $type, and load new controls at runtime via native ESM import().
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="12" cy="12" r="2.2" fill="currentColor" stroke="none"/><ellipse cx="12" cy="12" rx="10" ry="4.2"/><ellipse cx="12" cy="12" rx="10" ry="4.2" transform="rotate(60 12 12)"/><ellipse cx="12" cy="12" rx="10" ry="4.2" transform="rotate(120 12 12)"/></svg>
---

# Custom React Controls

The React renderer (`@meshweaver/react`) renders the **same `UiControl` tree** the Blazor portal renders: a layout area is delivered as one JSON object with an `areas` map (area key → control) and a `data` map (values that bindings point at), updated via RFC 7396 merge-patches. Every control carries a `$type` discriminator, and the renderer dispatches on it.

Extending the renderer therefore has exactly two halves:

1. **Server side** — a `UiControl` subclass registered with the hub, so your layout areas can emit it.
2. **React side** — a component registered for that `$type` in the renderer's control registry.

> **Note — the code on this page does not execute in the portal.** The live `--render` blocks used elsewhere in these docs run *C#* in the portal's kernel; React/TSX cannot execute there. The snippets below are plain fenced code, verified against the `@meshweaver/react` sources.

## 1. Define the control server-side

A custom control is a record deriving from `UiControl<TControl>`, exactly like the built-in ones. The two constructor arguments name the client module and its API version. This is the same pattern the shipped `GoogleMapControl` uses (`src/MeshWeaver.GoogleMaps`):

```csharp
using MeshWeaver.Layout;

namespace Acme.Widgets;

/// <summary>A tiny inline trend chart. Wire $type: "Sparkline".</summary>
public record SparklineControl() : UiControl<SparklineControl>("Acme.Widgets", "1.0.0")
{
    /// <summary>The numeric series to plot.</summary>
    public IReadOnlyCollection<double>? Data { get; init; }

    /// <summary>Stroke color (CSS color string).</summary>
    public string? Stroke { get; init; }
}
```

Register the type on every hub that serializes it, and — **only if the Blazor portal should also render it** — pair it with a Blazor view:

```csharp
public static MessageHubConfiguration AddSparkline(this MessageHubConfiguration configuration) =>
    configuration
        .WithTypes(typeof(SparklineControl))   // $type discriminator registration
        // Blazor-only: the React client needs no server-side view registration —
        // it resolves the $type in its own registry (next section).
        .AddViews(registry => registry.WithView<SparklineControl, SparklineView>());
```

Any layout area can now return it:

```csharp
public static UiControl Trend(LayoutAreaHost host, RenderingContext _) =>
    Controls.Stack
        .WithView(Controls.Title("Weekly volume", 2))
        .WithView(new SparklineControl { Data = [3, 5, 4, 9, 7, 12], Stroke = "#1e88e5" });
```

On the wire the control appears with its `$type` discriminator — **the class name minus the `Control` suffix** (`SparklineControl` → `"Sparkline"`, the same rule that maps `LabelControl` → `"Label"` and `DataGridControl` → `"DataGrid"`):

```json
{
  "areas": {
    "Trend/2": {
      "$type": "Sparkline",
      "data": [3, 5, 4, 9, 7, 12],
      "stroke": "#1e88e5",
      "moduleName": "Acme.Widgets",
      "apiVersion": "1.0.0",
      "skins": []
    }
  }
}
```

## 2. Register a React component for the `$type`

The renderer's dispatch is data-driven: `ControlRenderer` looks the control's `$type` up in the active **leaf pack** — a plain record of maps supplied through `RegistryProvider`:

```ts
// from @meshweaver/react (render/registryContext.tsx)
export type ControlComponent = (props: { control: UiControl }) => ReactNode;
export type SkinComponent = (props: { skin: Skin; control: UiControl }) => ReactNode;

export interface LeafPack {
  controls: Record<string, ControlComponent>;  // $type → component (leaf controls)
  skins: Record<string, SkinComponent>;        // skin $type → wrapper (Stack/Tabs/Card/…)
  fallback: ControlComponent;                  // renders an unknown $type
  defaultContainer: SkinComponent;             // container with no remaining skin
}
```

The shipped Fluent UI pack exports its registry as a spreadable object — *"Spread your own entries to extend or override"* is the designed extension point:

```tsx
import {
  MeshAreaView, RegistryProvider, RenderArea, ScopeProvider,
  controlRegistry, fluentPack, useResolve,
  type ControlComponent, type LeafPack, type UiControl,
} from "@meshweaver/react";

/** The React component for $type "Sparkline". */
const SparklineView: ControlComponent = ({ control }: { control: UiControl }) => {
  // useResolve handles both literal values and bound /data pointers —
  // the same resolution every built-in control uses.
  const data = (useResolve(control.data) as number[] | undefined) ?? [];
  const stroke = (useResolve(control.stroke) as string | undefined) ?? "currentColor";
  const max = Math.max(...data, 1);
  const points = data.map((v, i) => `${(i / Math.max(data.length - 1, 1)) * 100},${30 - (v / max) * 30}`).join(" ");
  return (
    <svg viewBox="0 0 100 30" width={100} height={30}>
      <polyline points={points} fill="none" stroke={stroke} strokeWidth={2} />
    </svg>
  );
};

/** The Fluent pack + our control: spread to extend (or override) $type entries. */
const myPack: LeafPack = {
  ...fluentPack,
  controls: { ...controlRegistry, Sparkline: SparklineView },
};
```

To render with a custom pack, compose the same three providers `MeshAreaView` composes — `RegistryProvider` (which pack), `ScopeProvider` (which `AreaSource` + root area), `RenderArea`:

```tsx
import { FluentProvider, webLightTheme } from "@fluentui/react-components";

export function App({ source }: { source: AreaSource }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <RegistryProvider pack={myPack}>
        <ScopeProvider source={source} area="Trend">
          <RenderArea areaKey="Trend" />
        </ScopeProvider>
      </RegistryProvider>
    </FluentProvider>
  );
}
```

(If the stock pack suffices, `<MeshAreaView source={source} rootArea="Trend" />` does all of the above with the built-in Fluent pack.)

### Where the data comes from — `AreaSource`

The renderer is transport-agnostic: it consumes an `AreaSource` — the `{areas, data}` tree plus an event sink:

```ts
// from @meshweaver/react (area/types.ts)
export interface AreaSource {
  getState(): AreaTree;                          // current {areas, data} snapshot
  subscribe(listener: () => void): () => void;   // change notification
  emit(event: MeshEvent): void;                  // clicks, edits, blur, closeDialog
}
```

- `StaticAreaSource` — an in-memory tree; ideal for developing a control against fixture JSON before touching a live portal.
- `GrpcAreaSource` — subscribes to a live layout area on a hub (`new GrpcAreaSource(connection, address, { area: "Trend" })`, then `source.start()`), folds every merge-patch into the tree, and posts `ClickedEvent`/pointer-update events back.

Inside your component, the binding hooks (`useResolve`, `useAreaState`, `useEmit`, `useScope`) give you the same two-way data binding the built-in controls use — a bound property is a `/data` pointer, edits are emitted as `update` events (`useEmit()({ kind: "update", area, pointer, value })`) and echoed back through the stream.

## 3. One React singleton — plugins receive the host's React

`@meshweaver/react` declares `react` and `react-dom` as **peerDependencies**, not dependencies. A control plugin must do the same:

```jsonc
// your plugin's package.json
{
  "peerDependencies": {
    "react": "^18.3.0",
    "react-dom": "^18.3.0"
  }
}
```

Never bundle React into a plugin. Two React copies in one page break the rules of hooks **and** context identity: the renderer hands your component the leaf pack and the area scope through React context (`RegistryProvider`, `ScopeProvider`) — a component rendered by a *different* React instance sees `null` context and throws (`"MeshWeaver control rendered without a leaf pack"`). The host application owns the single React; plugins compile against it and receive it at runtime.

## 4. Runtime loading — native ESM `import(url)`

Because a pack is plain data (`Record<string, ControlComponent>`), controls can be added **without rebuilding the host**: publish the plugin as an ES module and load it with the browser's native dynamic `import()`:

```tsx
// The plugin module's contract: default-export its $type → component entries.
// (plugin: export default { Sparkline: SparklineView } satisfies Record<string, ControlComponent>)
const module = await import(/* @vite-ignore */ pluginUrl);

const pack: LeafPack = {
  ...fluentPack,
  controls: { ...controlRegistry, ...module.default },
};
```

Two requirements for this to work:

1. **React must stay external.** Build the plugin with `react`/`react-dom` (and `@meshweaver/react`, if imported) marked as externals, and serve them to the browser via an [import map](https://developer.mozilla.org/en-US/docs/Web/HTML/Element/script/type/importmap) so the plugin's `import "react"` resolves to the host's singleton — this is rule 3 enforced at the module-graph level.
2. **Re-render with the new pack.** A pack is ordinary React state — load the module, merge its entries, set state. Controls whose `$type` arrived from the server before the plugin loaded render through `fallback` until then; the swap re-renders them for real.

## Recap

| Step | Server (C#) | Client (React) |
|---|---|---|
| Define | `record SparklineControl : UiControl<SparklineControl>(...)` | `const SparklineView: ControlComponent = ...` |
| Register | `.WithTypes(typeof(SparklineControl))` | `controls: { ...controlRegistry, Sparkline: SparklineView }` |
| Render | return it from any layout area | `RegistryProvider` + `ScopeProvider` + `RenderArea` (or `MeshAreaView` for the stock pack) |
| Ship | part of your hub module | ESM module, React as peer/external, loadable via `import(url)` |

## Related

- [Layout Areas](../LayoutAreas) — how the hub decides what to render where.
- [Data Binding](../DataBinding) — the `/data` pointer contract the binding hooks implement.
- [Container Controls](../ContainerControl) — containers render via *skins* (`LeafPack.skins`), not the control map.
- [User Interface architecture](/Doc/Architecture/UserInterface) — the control-tree model shared by Blazor, MAUI, and React.
