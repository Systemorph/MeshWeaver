// The MeshWeaver layout-area wire model. A layout area is delivered as a single JSON object with an
// `areas` map (area-key -> control) and a `data` map (values that bindings point at), updated via
// RFC 7396 merge-patches. Controls carry `$type` (short name: "Stack", "Label", "DataGrid", ...),
// the base props below, and — for containers — an `areas` list of NamedArea references.

export type Json = any;

export interface UiControl {
  $type: string;
  id?: Json;
  dataContext?: string;
  style?: Json;
  class?: Json;
  skins?: Skin[];
  isClickable?: boolean;
  pageTitle?: Json;
  [key: string]: Json;
}

export interface Skin {
  $type: string;
  [key: string]: Json;
}

export interface NamedArea extends UiControl {
  $type: "NamedArea";
  area?: string;
  showProgress?: boolean;
  spinnerType?: string;
}

export interface AreaTree {
  areas?: Record<string, UiControl>;
  data?: Record<string, Json>;
  [key: string]: Json;
}

export interface MeshEvent {
  kind: "click" | "blur" | "update" | "closeDialog";
  area: string;
  pointer?: string;
  /** For "update": the new value. For "closeDialog": the DialogCloseState ("OK" | "Cancel"). */
  value?: Json;
}

/** A subscribable source of an area tree + an event sink — implemented by the static demo source
 *  and by the gRPC-backed live source. The renderer depends only on this. */
export interface AreaSource {
  getState(): AreaTree;
  subscribe(listener: () => void): () => void;
  emit(event: MeshEvent): void;
  /** The raw error string when the subscription faulted / ended before a snapshot (access denied,
   *  node gone, transient miss), or null/undefined while healthy. Set by GrpcAreaSource and notified
   *  to subscribers; shells classify it via `classifyAreaError` (area/accessError). Optional so the
   *  in-memory StaticAreaSource — which never faults — need not declare it. */
  readonly error?: string | null;
}
