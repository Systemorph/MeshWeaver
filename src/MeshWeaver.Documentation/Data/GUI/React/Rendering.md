---
Name: React Rendering Architecture
Category: Documentation
Description: How the React frontend renders a layout area — the UiControl JSON contract, the $type→component registry and skin dispatch, MeshAreaView + AreaSource, and live hydration over the gRPC-web Connect+Deliver split.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/></svg>
---

# React Rendering Architecture

The renderer's design in one sentence: a **transport-free core** walks the `{areas, data}` `UiControl` tree, dispatching each control's `$type` against a swappable component registry — and everything live (subscriptions, patches, events) is hidden behind one small interface, `AreaSource`.

```
        layout-area stream  ({areas, data} UiControl tree + patches)
                    │
          ┌─────────┴─────────┐
          │  renderer core    │   dispatch on $type, pop skins, resolve bindings, post events
          │ (transport-free)  │   ← written ONCE (clients/react/src/render + area)
          └─────────┬─────────┘
       DOM leaf pack        RN leaf pack
   web / Electron        iOS / Android
```

This is the same shape the platform uses everywhere: MAUI renders the identical tree with a native `MauiViewPack`, Blazor with its web views — the React renderer adds a Fluent UI React v9 pack (near 1:1 with the Blazor portal's Fluent UI Blazor components).

## The wire model: `UiControl` trees

A layout area is delivered as a single JSON object with an `areas` map (area key → control) and a `data` map (values that bindings point at). From `clients/react/src/area/types.ts`:

```ts
export interface UiControl {
  $type: string;              // short name: "Stack", "Label", "DataGrid", ...
  id?: Json;
  dataContext?: string;       // pointer prefix for relative bindings
  style?: Json;
  class?: Json;
  skins?: Skin[];             // container/wrapper skins, popped LIFO
  isClickable?: boolean;
  pageTitle?: Json;
  [key: string]: Json;
}

export interface AreaTree {
  areas?: Record<string, UiControl>;
  data?: Record<string, Json>;
}
```

Containers don't inline their children — they carry an `areas` list of `NamedArea` references, each pointing at another key in the `areas` map. A control property is either a literal value or a **binding**: a JSON pointer into `/data`, resolved (and written back) live. This is exactly the contract described in [Data Binding](/Doc/GUI/DataBinding); the React renderer is another consumer of it.

## `AreaSource` — the one seam

The renderer depends on nothing but this interface:

```ts
export interface AreaSource {
  getState(): AreaTree;                          // current {areas, data} snapshot
  subscribe(listener: () => void): () => void;   // change notification
  emit(event: MeshEvent): void;                  // clicks, edits, blur, closeDialog
}
```

Two implementations ship:

- **`StaticAreaSource`** (`area/source.ts`) — an in-memory tree. Drives the demo and the tests; an `"update"` event optimistically writes the value at its pointer, exactly as a live stream would echo the merge-patch back. Ideal for developing a control against fixture JSON before touching a live portal.
- **`GrpcAreaSource`** (`live/grpcSource.ts`) — subscribes to a live layout area on a hub and folds the stream into the tree (next section).

## `MeshAreaView` — the composed entry point

`MeshAreaView` (the package's top-level component, `clients/react/src/index.tsx`) stacks the four providers the renderer needs and renders the root area:

```tsx
export function MeshAreaView({ source, rootArea, theme, themeStorageKey, ops }: MeshAreaViewProps) {
  const { theme: preferredTheme } = useThemeMode({ storageKey: themeStorageKey });
  return (
    <FluentProvider theme={theme ?? preferredTheme}>
      <MeshOpsProvider ops={ops ?? null}>
        <RegistryProvider pack={fluentPack}>
          <ScopeProvider source={source} area={rootArea}>
            <RenderArea areaKey={rootArea} />
          </ScopeProvider>
        </RegistryProvider>
      </MeshOpsProvider>
    </FluentProvider>
  );
}
```

- `FluentProvider` supplies the design tokens (see [Theming](../Theming)).
- `MeshOpsProvider` supplies the optional mesh-operations surface controls like `ThreadChat` need beyond the area contract (see [Thread Chat](../ThreadChat)).
- `RegistryProvider` installs the **leaf pack**; `ScopeProvider` + `RenderArea` scope the source and area key.

## The registry: `$type` → component

Dispatch is data-driven. The active pack is a plain record of maps (`render/registryContext.tsx`):

```ts
export type ControlComponent = (props: { control: UiControl }) => ReactNode;
export type SkinComponent = (props: { skin: Skin; control: UiControl }) => ReactNode;

export interface LeafPack {
  controls: Record<string, ControlComponent>;  // $type → component (leaf controls)
  skins: Record<string, SkinComponent>;        // skin $type → wrapper (Stack/Tabs/Card/…)
  fallback: ControlComponent;                  // renders an unknown $type
  defaultContainer: SkinComponent;             // container with no remaining skin
}
```

The shipped web pack is `fluentPack` (`render/registry.tsx`): `controlRegistry` spreads the per-category maps (display, inputs, data, nav, feedback, containers, editors, mesh, appearance, item templates) and `skinRegistry` covers the container skins. Spreading your own entries over `controlRegistry` is the designed extension point — see [Custom React Controls](/Doc/GUI/ReactCustomControls).

`ControlRenderer` (`render/ControlRenderer.tsx`) implements the dispatch:

1. **Skins pop LIFO** — the last skin wraps (or, for layout skins, lays out) the control, recursing with the rest.
2. A **container with no remaining skin** renders through `defaultContainer` (a flex stack).
3. A **leaf** dispatches `$type` against `pack.controls`; unknown types render `pack.fallback` — a clearly-labeled "Unsupported control", never a crash.

The lookup tolerates both `$type` spellings: registries hold the suffix-stripped short names (`"Stack"`, `"LayoutStack"`), while the live mesh serializes class names (`"StackControl"`, `"LayoutStackSkin"`) — the exact key is tried first, then the suffix-stripped one.

Child areas resolve through the same primitives your own controls can use:

```tsx
export { ControlRenderer, RenderArea, RenderChildren, useChildAreas } from "./render/ControlRenderer.js";
export { ScopeProvider, useAreaState, useResolve, useEmit, useScope } from "./area/context.js";
```

`useResolve(control.someProp)` returns the property's value whether it is a literal or a bound `/data` pointer; `useEmit()` posts events into the source; `useAreaState()` is the live tree (backed by `useSyncExternalStore`, so React re-renders exactly the consumers of changed state).

## Live hydration: `GrpcAreaSource` over Connect + Deliver

Browsers can't open the mesh's bidirectional gRPC `Open` stream (no HTTP/2 duplex from `fetch`), so the server splits the duplex into a **server-streaming `Connect`** (mesh → client) and a **unary `Deliver`** (client → mesh). `MeshWebConnection` in `@meshweaver/client-web` (`clients/grpc-web/src/connection.ts`) hides the split behind the same surface the Node SDK exposes — `observe` / `post` / `watch`:

- `connect(url, { token })` opens the `Connect` stream; the first frame is an **ack carrying the `connection_id`** that every subsequent `Deliver` quotes back. The bearer token rides in gRPC-web call metadata; identity is stamped server-side, never claimed by the client.
- **The `Connect` stream auto-reconnects.** If it drops (proxy idle-close, network blip, a truncating port-forward), `MeshWebConnection` re-opens it and **re-posts every active `SubscribeRequest`**, so a stream that was mid-delivery gets a fresh `Full` frame; already-delivered frames dedup by `version` (below), so replay completes the page without disturbing what already rendered. Only an explicit `close()` stops it.
- Each browser tab joins the mesh as a **stream-routed participant**. The connection *defaults* to `node/<id>` — a foreign-participant address type declared by `AddGrpcHub` (`src/MeshWeaver.Hosting.Grpc/GrpcHostingExtensions.cs`) — but the portal shell overrides it with a **stable per-tab `portal/<id>`** (a built-in stream-routed type, GUID in `sessionStorage`) so the server keeps the participant and its per-stream sync sub-hubs alive across reloads. The overview's [Live connection & session](/Doc/GUI/React) explains why that stability matters.
- Received frames are demuxed: responses correlate to pending `observe` requests by `RequestId`; stream messages route to the matching `watch` subscription by `streamId`.

`GrpcAreaSource` builds the area subscription on `watch`:

```ts
import { connect } from "@meshweaver/client-web";
import { GrpcAreaSource, MeshAreaView } from "@meshweaver/react";

const conn = await connect("https://memex.meshweaver.cloud", { token: "mw_..." });
const source = new GrpcAreaSource(conn, "ACME/MyApp", { area: "Overview" });
void source.start();   // folds the live area stream into {areas, data}
// <MeshAreaView source={source} rootArea="Overview" />
```

The wire behavior it pins (documented in `live/grpcSource.ts` against the server sources):

- The subscribe message is a `SubscribeRequest` whose `reference` **must carry the polymorphic `$type` discriminator** (`"LayoutAreaReference"`) — the server deserializes an abstract `WorkspaceReference` and drops the subscribe without it.
- An **empty `reference.area` subscribes the default area**: the first `Full` frame carries `areas[""]` as a `NamedArea` pointing at the resolved area, and rendering root key `""` follows the indirection.
- `ChangeType "Full"` is a whole snapshot; `"Patch"` is an **RFC 6902 JSON-patch array** (not an RFC 7396 merge), folded immutably into the tree.
- **Frames fold by a monotonic `version`.** Every `DataChangedEvent` carries a per-stream `version`; `GrpcAreaSource` drops any frame at a version it has already folded (a duplicate re-send, or a stale/reordered frame) and applies a `Patch` only once a `Full` snapshot has set a base. This is what makes reconnect-replay safe and kills the "different subset every refresh" non-determinism — the transport can deliver duplicates with no cross-frame ordering guarantee, so folding one twice would make the render depend on arrival timing.
- Snapshot collections arrive as `InstanceCollections` with JSON-encoded instance keys (`"Content"` arrives as the property `"\"Content\""`); keys are decoded at fold time so area lookups use plain names.
- Events post back as mesh messages: `click` → `ClickedEvent`, `blur` → `BlurEvent`, `closeDialog` → `CloseDialogEvent`, and `update` → `PatchDataChangeRequest` carrying a one-op RFC 6902 `replace` at the binding's pointer. Updates apply **optimistically** to the local tree; the owning hub applies the patch and echoes the authoritative change back down the stream.

**One registry per connection.** A single `MeshAreaRegistry` (`live/grpcSource.ts`) owns every `GrpcAreaSource` over a connection: the routed page area *and* every nested `@@` / `LayoutAreaControl` embed resolve through it, so a given `(address, area, id)` has exactly one live `watch` stream shared by all consumers (`createGrpcEmbeddedFactory` is its embed-factory view). Creating a fresh source per render or per embed re-subscribes on every re-render — the same non-determinism the version fold guards against, one level up. See the overview's [Live connection & session](/Doc/GUI/React) for the session-level picture.

That is why a transport blip doesn't kill the UI the way a dropped Blazor circuit does: the browser holds the whole `{areas, data}` state, and the connection's auto-reconnect re-opens the subscription and replays it, yielding a fresh `Full` snapshot to converge on — nothing server-side needs the old connection's memory.

## Related

- [React Frontend overview](/Doc/GUI/React) — the package map and parity state.
- [Custom React Controls](/Doc/GUI/ReactCustomControls) — registering components for your own `$type`s.
- [Layout Areas](/Doc/GUI/LayoutAreas) — how the hub decides what each area shows.
- [Data Binding](/Doc/GUI/DataBinding) — the `/data` pointer contract `useResolve` implements.
