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
  MeshAreaRegistry,
  createGrpcEmbeddedFactory,
  type MeshConnectionLike,
  type LayoutAreaReference,
  type GrpcAreaOptions,
} from "./live/grpcSource.js";
export { normalizeEntityStore } from "./live/wire.js";
// Interactive-markdown parsing — the ONE shared, non-proprietary parser (twin of the server Markdig
// pipeline). splitRenderedHtml hydrates the server-rendered HTML (render-markdown) into text chunks +
// @@ area markers + code-cell toolbars; both the web pack and the RN pack render those with their own leaves.
export {
  splitRenderedHtml,
  parseInteractiveMarkdown,
  cellSubmissions,
  KERNEL_ADDRESS_PLACEHOLDER,
  type RenderedHtmlSegment,
  type HtmlChunkSegment,
  type HtmlAreaSegment,
  type HtmlToolbarSegment,
  type HtmlMermaidSegment,
  type InteractiveSegment,
} from "./controls/interactiveMarkdown.js";
export {
  MeshOpsProvider,
  useMeshOps,
  type MeshOps,
  type MeshNodeState,
  type ThreadSubmitOptions,
  type MarkdownCellSubmission,
  type MarkdownKernelSession,
  type RenderedMarkdown,
  type DocumentDownload,
  type DocumentExportOptions,
  type ContentListing,
  type ContentItem,
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
// Area-error classification — the TS twin of the server AreaErrorClassifier. Shells use these to make
// the SAME access-denied / redirect / node-gone decisions the Blazor NamedAreaView makes.
export {
  classifyAreaError,
  getAccessDeniedPath,
  isAccessDenied,
  isNodeGone,
  isSafeRedirect,
  type AreaErrorInfo,
  type AreaErrorKind,
} from "./area/accessError.js";
// Field/option binding + string coercion — shared by ALL leaf packs (the RN pack used to
// re-implement these because they weren't exported from the core).
export { str, useClick, useField, useOptions, useText, type Field } from "./controls/common.js";
// Auto-saving editors (MarkdownEditorControl / CodeEditorControl `autoSaveAddress`): the debounced
// write path a node-bound editor needs, shared by every leaf pack so the web and native editors
// cannot persist differently — or, as before #1476, not at all.
export {
  AUTO_SAVE_DEBOUNCE_MS,
  autoSaveAddressOf,
  autoSaveContentPatch,
  useAutoSave,
  type AutoSaveKind,
} from "./controls/autoSave.js";
// .NET-style value formatting and the PURE pivot (cross-tab) model. Renderer-free on purpose: both
// the Fluent pack and the React Native pack aggregate and format through THESE, so a pivot total is
// computed by one implementation rather than one per platform.
export { formatValue } from "./controls/format.js";
// The analysis views' geometry (#745) — the ports of TowerControl.Layout / ComparisonBarsControl.Layout.
// Renderer-free for exactly the reason formatValue is: both the Fluent pack and the React Native pack
// place a tower band and size a comparison bar through THESE, so the two draw the same picture.
export {
  analysisRows,
  clamp,
  comparisonLayout,
  navigableHref,
  num,
  optionalNum,
  towerLayout,
  towerTicks,
  type ComparisonPairWire,
  type ComparisonPlacement,
  type ComparisonRow,
  type TowerBandPlacement,
  type TowerBandWire,
  type TowerPlacement,
} from "./controls/analysisLayout.js";
export {
  computePivot,
  formatCell,
  groupsAt,
  type PivotAggregateWire,
  type PivotConfigWire,
  type PivotDimensionWire,
  type PivotResult,
  type PivotRowOut,
} from "./controls/pivotModel.js";
// Icon-value classification (pure logic, no DOM) — the one decision table every pack and shell
// dispatches icons through; each platform renders the classified kinds with its own leaves.
export {
  classifyIcon,
  iconForRendering,
  iconNameOf,
  isEmojiIcon,
  isFluentIconName,
  isIconUrl,
  isInlineSvg,
  sanitizeInlineSvg,
  type ClassifiedIcon,
  type IconKind,
} from "./controls/iconValue.js";
// The vendored Blazor github-markdown CSS (pure strings) — web injects it via meshStyles, the RN
// web shell injects it verbatim.
export { githubMarkdownCss, githubMarkdownDarkCss, scopeMarkdownCss } from "./theme/githubMarkdown.js";
// Localization — the client twin of AccessService.Localize over the SAME strings.{en,de}.json
// catalog the server ships (kept identical by i18n/localize.test.ts). Every leaf pack and shell
// resolves user-visible text through these, never a hard-coded literal.
export {
  DEFAULT_LOCALE,
  LOCALE_DISPLAY_NAMES,
  SUPPORTED_LOCALES,
  catalogKeys,
  localize,
  localizePlural,
  resolveLocale,
  tryMatchLocale,
} from "./i18n/localize.js";
export { LocaleProvider, useLocale, useLocalize, type LocaleContextValue } from "./i18n/LocaleContext.js";
