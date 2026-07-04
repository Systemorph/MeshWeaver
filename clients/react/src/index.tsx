// @meshweaver/react — render a MeshWeaver layout area as React + Fluent UI. The renderer consumes an
// AreaSource (the {areas,data} tree + an event sink), so it's transport-agnostic: feed it the static
// demo source, or the gRPC-backed live source. This is the web/Electron/React-Native-capable analog of
// the MAUI MauiViewPack — the SAME UiControl tree, a Fluent React leaf pack.

import { FluentProvider, type Theme } from "@fluentui/react-components";
import type { AreaSource } from "./area/types.js";
import { ScopeProvider } from "./area/context.js";
import { RenderArea } from "./render/ControlRenderer.js";
import { RegistryProvider } from "./render/registryContext.js";
import { fluentPack } from "./render/registry.js";
import { useThemeMode } from "./theme/themeMode.js";
import { MeshOpsProvider, type MeshOps } from "./live/meshOps.js";

export interface MeshAreaViewProps {
  source: AreaSource;
  /** Key of the root area to render (a key in the source's `areas` map). */
  rootArea: string;
  /** Pin a Fluent theme. When omitted the view follows the user's persisted light/dark/system
   *  preference (localStorage "theme", the same key the Blazor portal uses) — see useThemeMode. */
  theme?: Theme;
  /** Override the theme localStorage key (defaults to the Blazor-compatible "theme"). */
  themeStorageKey?: string;
  /** The mesh operations surface (node watches + thread submission) — controls that go beyond the
   *  layout-area contract (ThreadChat) need it. `Mesh.from(connection)` from @meshweaver/client-web
   *  satisfies it structurally. */
  ops?: MeshOps | null;
}

/** Top-level: wraps Fluent's provider and renders the root layout area. The provider fills its
 *  host (height 100%) so full-height layouts (chat panes, splitters) reach the page bottom —
 *  without it the area collapses to content height (the "page height is not 100%" parity bug). */
export function MeshAreaView({ source, rootArea, theme, themeStorageKey, ops }: MeshAreaViewProps) {
  const { theme: preferredTheme } = useThemeMode({ storageKey: themeStorageKey });
  return (
    <FluentProvider theme={theme ?? preferredTheme} style={{ height: "100%", minHeight: 0 }}>
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

export { ControlRenderer, RenderArea, RenderChildren, useChildAreas } from "./render/ControlRenderer.js";
export { controlRegistry, FallbackControl, fluentPack, type ControlComponent } from "./render/registry.js";
export { skinRegistry } from "./render/skins.js";
export { RegistryProvider, useLeafPack, type LeafPack, type SkinComponent } from "./render/registryContext.js";
export { StaticAreaSource } from "./area/source.js";
export {
  GrpcAreaSource,
  createGrpcEmbeddedFactory,
  type MeshConnectionLike,
  type LayoutAreaReference,
  type GrpcAreaOptions,
} from "./live/grpcSource.js";
export { normalizeEntityStore } from "./live/wire.js";
export {
  MeshOpsProvider,
  useMeshOps,
  type MeshOps,
  type MeshNodeState,
  type ThreadSubmitOptions,
} from "./live/meshOps.js";
export { ScopeProvider, useAreaState, useResolve, useEmit, useScope } from "./area/context.js";
export {
  NavigationProvider,
  useNavigation,
  useMeshLink,
  useHtmlLinkInterceptor,
  isExternalTarget,
  type MeshNavigation,
  type MeshLink,
} from "./area/navigation.js";
export {
  EmbeddedAreaProvider,
  useAreaSourceFactory,
  type AreaSourceFactory,
  type EmbeddedAreaHandle,
  type EmbeddedAreaReference,
} from "./render/embeddedArea.js";
export { getPointer, setPointer, mergePatch, resolve } from "./area/pointer.js";
export type { AreaSource, AreaTree, UiControl, Skin, NamedArea, MeshEvent, Json } from "./area/types.js";
export {
  useThemeMode,
  readStoredThemeMode,
  writeStoredThemeMode,
  clearStoredTheme,
  resolveThemeMode,
  fluentThemeFor,
  systemPrefersDark,
  DEFAULT_THEME_STORAGE_KEY,
  type ThemeMode,
  type ResolvedThemeMode,
  type ThemeState,
  type UseThemeModeOptions,
} from "./theme/themeMode.js";
export { ThemeToggle, type ThemeToggleProps } from "./theme/ThemeToggle.js";
