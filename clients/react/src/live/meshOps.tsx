// The mesh OPERATIONS surface a live control needs beyond the layout-area AreaSource contract:
// watching arbitrary NODE streams (a thread node + its per-message satellite cells) and the canonical
// thread-submission ops (the client twin of MeshWeaver.AI.HubThreadExtensions — StartThread /
// SubmitMessage). The react package stays transport-free (the same decoupling GrpcAreaSource gets via
// MeshConnectionLike): @meshweaver/client-web's `Mesh` satisfies this interface STRUCTURALLY, so the
// host app wires it with
//
//   import { Mesh } from "@meshweaver/client-web";
//   const mesh = Mesh.from(connection);          // or await Mesh.connect(url, { token })
//   <MeshOpsProvider ops={mesh}> ... <RenderArea/> ... </MeshOpsProvider>
//
// and every wire shape (CreateNodeRequest for StartThread, RFC 7396 PatchDataRequest for
// SubmitMessage / control-plane flips) is produced — and tested — in @meshweaver/client-web.

import { createContext, useContext } from "react";
import type { ReactNode } from "react";
import type {
  MarkdownCellSubmission,
  MarkdownKernelSession,
  RenderedMarkdown,
} from "../controls/interactiveMarkdown.js";

/** One emission of a node's live stream (the shape meshNodeFromChange yields). */
export interface MeshNodeState {
  path: string;
  name?: string;
  nodeType?: string;
  content: Record<string, unknown>;
  [key: string]: unknown;
}

export interface ThreadSubmitOptions {
  agentName?: string;
  modelName?: string;
  harness?: string;
  contextPath?: string;
  attachments?: string[];
  createdBy?: string;
  authorName?: string;
}

export interface MeshOps {
  /** Subscribe to a node's live state — yields on every change (initial state, then updates). */
  watch(path: string): AsyncIterable<MeshNodeState>;
  /**
   * Create a thread under `namespacePath` with the first user message queued — ONE CreateNodeRequest
   * carrying the seeded thread node (content.pendingUserMessages + composer), targeted at the
   * namespace hub. The per-thread submission watcher dispatches the first round.
   */
  startThread(
    namespacePath: string,
    userText: string,
    opts?: ThreadSubmitOptions,
  ): Promise<{ path: string; userMessageId?: string }>;
  /**
   * Queue a user message on an existing thread — a JSON-merge PatchDataRequest appending to
   * content.userMessageIds + content.pendingUserMessages (the client twin of
   * `GetMeshNodeStream(threadPath).Update(...)`). Resolves to the new message id, or null for a
   * whitespace-only no-op.
   */
  submitMessage(threadPath: string, userText: string, opts?: ThreadSubmitOptions): Promise<string | null>;
  /** Field-level partial node update (RFC 7396) — control-plane flips like requestedStatus. */
  patch(path: string, fields: Record<string, unknown>): void;
  /** Optional mesh query — when present it feeds the agent/model selectors (nodeType:Agent / nodeType:Model). */
  search?(query: string, basePath?: string, limit?: number): Promise<Record<string, unknown>[]>;
  /**
   * Optional streaming-final autocomplete snapshot (the one-shot AutocompleteRequest twin) — feeds
   * the composer's @-mention dropdown (the Blazor MeshNodeAutocomplete parity surface). Hosts
   * without it render the composer with no @-suggestions (no crash).
   */
  autocomplete?(query: string, contextPath?: string): Promise<AutocompleteSuggestion[]>;
  /**
   * Optional interactive-markdown kernel bootstrap — the client twin of
   * <c>MarkdownViewLogic.CreateActivityAndSubmit</c>: creates the per-view kernel Activity under
   * the VIEWER's partition, waits until it is routable (the subscribe-before-create storm guard),
   * posts the initial cell submissions IN ORDER, and resolves to the live session whose result
   * areas the executable code cells embed. Hosts without it render the cells with the
   * "execution unavailable" notice (no crash, no phantom subscriptions).
   */
  startMarkdownKernel?(cells: MarkdownCellSubmission[]): Promise<MarkdownKernelSession>;
  /**
   * Optional server-side markdown render — THE Markdig pipeline (the one parser: executable code
   * blocks, layout-area embeds, @@ macros, UCR links, mermaid). The client renders the returned
   * HTML and hydrates the interactive markers (see splitRenderedHtml); it never re-parses
   * markdown itself. Hosts without it fall back to the plain client-side renderer.
   */
  renderMarkdown?(markdown: string, nodePath?: string): Promise<RenderedMarkdown>;
  /** Optional single-node read — the raw hub-serialized MeshNode JSON at `path` (NodeExport bundles
   *  a subtree of these). Null when absent/unreadable. */
  getNode?(path: string): Promise<Record<string, unknown> | null>;
  /** Optional node create — the NodeImport fan-out re-creates each bundled node via this (routes to
   *  the node's owner partition, throws on a refused create). */
  createNode?(node: Record<string, unknown>): Promise<void>;
  /**
   * Optional document export — the client twin of the Blazor ExportDocumentView: posts
   * ExportDocumentRequest to the source node hub, watches the returned Activity to a terminal
   * status, and resolves the rendered PDF/DOCX bytes for a browser download. Absent → the control
   * renders a "not available here" notice.
   */
  exportDocument?(sourcePath: string, options: DocumentExportOptions): Promise<DocumentDownload>;
  /** Optional content-collection listing — the read half of the file browser. `path` is
   *  `{node}/{collection}[/{dir}]`. Absent → the browser renders a "not available" notice. */
  listContent?(path: string): Promise<ContentListing>;
  /** Optional content upload (multipart) — `path` is `{node}/{collection}/{filePath}`. */
  uploadContent?(path: string, file: File): Promise<void>;
  /**
   * Optional speech-to-text — POSTs recorded audio (multipart) to the mesh's
   * `POST /api/speech/transcribe` (the ISpeechTranscriber → Whisper surface every backend now bakes in:
   * the portal AND the local sidecar). The composer shows a mic button only when a host wires this;
   * absent → no mic (no crash). Returns the recognized transcript.
   */
  transcribe?(audio: Blob, language?: string): Promise<{ text: string; language?: string }>;
}

/** A content-collection directory listing (the /api/mesh/content/list shape). */
export interface ContentListing {
  collection: string;
  path: string;
  editable: boolean;
  items: ContentItem[];
}

/** One entry in a content-collection directory. */
export interface ContentItem {
  kind: "folder" | "file" | "unknown";
  name: string;
  path: string;
  itemCount?: number;
  lastModified?: string;
}

/** A rendered export ready for a browser download (the RenderedDocument wire shape, decoded). */
export interface DocumentDownload {
  fileName: string;
  mimeType: string;
  bytes: Uint8Array;
}

/** The subset of DocumentExportOptions the React export form drives. */
export interface DocumentExportOptions {
  format?: "pdf" | "docx";
  title?: string;
  includeChildren?: boolean;
  coverPage?: boolean;
  tableOfContents?: boolean;
}

export type { MarkdownCellSubmission, MarkdownKernelSession, RenderedMarkdown } from "../controls/interactiveMarkdown.js";

/** One @-mention suggestion (the wire AutocompleteItem, camelCase). */
export interface AutocompleteSuggestion {
  label?: string;
  insertText?: string;
  description?: string;
  icon?: string;
  path?: string;
}

const MeshOpsCtx = createContext<MeshOps | null>(null);

/** The MeshOps of the nearest provider, or null when the app renders without a live mesh connection. */
export function useMeshOps(): MeshOps | null {
  return useContext(MeshOpsCtx);
}

export function MeshOpsProvider({ ops, children }: { ops: MeshOps | null; children: ReactNode }) {
  return <MeshOpsCtx.Provider value={ops}>{children}</MeshOpsCtx.Provider>;
}
