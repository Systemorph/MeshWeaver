// @meshweaver/react — render a MeshWeaver layout area as React + Fluent UI. The renderer consumes an
// AreaSource (the {areas,data} tree + an event sink), so it's transport-agnostic: feed it the static
// demo source, or the gRPC-backed live source. This is the web/Electron/React-Native-capable analog of
// the MAUI MauiViewPack — the SAME UiControl tree, a Fluent React leaf pack.

import { FluentProvider, webLightTheme, type Theme } from "@fluentui/react-components";
import type { AreaSource } from "./area/types.js";
import { ScopeProvider } from "./area/context.js";
import { RenderArea } from "./render/ControlRenderer.js";

export interface MeshAreaViewProps {
  source: AreaSource;
  /** Key of the root area to render (a key in the source's `areas` map). */
  rootArea: string;
  theme?: Theme;
}

/** Top-level: wraps Fluent's provider and renders the root layout area. */
export function MeshAreaView({ source, rootArea, theme }: MeshAreaViewProps) {
  return (
    <FluentProvider theme={theme ?? webLightTheme}>
      <ScopeProvider source={source} area={rootArea}>
        <RenderArea areaKey={rootArea} />
      </ScopeProvider>
    </FluentProvider>
  );
}

export { ControlRenderer, RenderArea, RenderChildren, useChildAreas } from "./render/ControlRenderer.js";
export { controlRegistry, FallbackControl, type ControlComponent } from "./render/registry.js";
export { skinRegistry } from "./render/skins.js";
export { StaticAreaSource } from "./area/source.js";
export {
  GrpcAreaSource,
  type MeshConnectionLike,
  type LayoutAreaReference,
  type GrpcAreaOptions,
} from "./live/grpcSource.js";
export { ScopeProvider, useAreaState, useResolve, useEmit, useScope } from "./area/context.js";
export { getPointer, setPointer, mergePatch, resolve } from "./area/pointer.js";
export type { AreaSource, AreaTree, UiControl, Skin, NamedArea, MeshEvent, Json } from "./area/types.js";
