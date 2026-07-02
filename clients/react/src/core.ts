// Platform-agnostic renderer core — NO Fluent / DOM imports. A React Native (or any non-DOM) app
// imports from here and supplies its OWN leaf pack via <RegistryProvider pack={…}>. The web entry
// (`@meshweaver/react`) is this core + the Fluent DOM pack pre-installed by <MeshAreaView>.

export { ControlRenderer, RenderArea, RenderChildren, useChildAreas } from "./render/ControlRenderer.js";
export {
  RegistryProvider,
  useLeafPack,
  type LeafPack,
  type ControlComponent,
  type SkinComponent,
} from "./render/registryContext.js";
export { ScopeProvider, useAreaState, useResolve, useBindingPointer, useEmit, useScope } from "./area/context.js";
export { StaticAreaSource } from "./area/source.js";
export {
  GrpcAreaSource,
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
export {
  EmbeddedAreaProvider,
  useAreaSourceFactory,
  type AreaSourceFactory,
  type EmbeddedAreaHandle,
  type EmbeddedAreaReference,
} from "./render/embeddedArea.js";
export { getPointer, setPointer, mergePatch, resolve, bindingPointer } from "./area/pointer.js";
export type { AreaSource, AreaTree, UiControl, Skin, NamedArea, MeshEvent, Json } from "./area/types.js";
