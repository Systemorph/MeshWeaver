// Interactive-markdown parsing — the React twin of MeshWeaver.Markdown's ExecutableCodeBlock
// grammar (src/MeshWeaver.Markdown/ExecutableCodeBlockExtension.cs + Renderer):
//
//   ```csharp --render <AreaId> [--show-code|--show-header]   → execute + stream the result into
//        the named layout area of the per-view KERNEL activity (the viewer-owned
//        {user}/_Activity/markdown-{id} node — see MarkdownViewLogic.CreateActivityAndSubmit).
//   ```csharp --execute [id]                                  → silent execution (no output area).
//   ```mermaid                                                → a mermaid diagram.
//   ```layout  (yaml-ish address/area/id)                     → embed an EXISTING layout area.
//   @@("area/X") / @@("path")  on its own line, OUTSIDE fences → layout-area embed macro.
//
// The parser is pure and ordered: it walks the markdown ONCE, extracting fenced blocks FIRST (an
// @@ macro inside a fence stays literal — fences win), then splitting the remaining text chunks at
// the @@ macro lines. Rendering/execution live in display.tsx / the MeshOps host.

export interface MarkdownTextSegment {
  kind: "markdown";
  text: string;
}

export interface AreaEmbedSegment {
  kind: "embed";
  /** "area/X" (an area of the owning node) or a node path (its default area). */
  path: string;
}

export interface LayoutEmbedSegment {
  kind: "layout";
  address: string;
  area: string;
  id?: string;
}

export interface MermaidSegment {
  kind: "mermaid";
  code: string;
}

export interface CodeCellSegment {
  kind: "cell";
  language: string;
  code: string;
  /** The submission id = the kernel result-area name (--render <id>), or the --execute id. */
  submissionId: string;
  /** True when the cell streams output into a result area (--render). --execute cells are silent. */
  hasOutput: boolean;
  showCode: boolean;
  showHeader: boolean;
  /** The raw fence header line (shown when --show-header). */
  header: string;
}

export type InteractiveSegment =
  | MarkdownTextSegment
  | AreaEmbedSegment
  | LayoutEmbedSegment
  | MermaidSegment
  | CodeCellSegment;

/** Block-level layout-area macro (outside fences): @@("area/X"), @@("path"), @@"path", @@path. */
const AREA_MACRO_LINE = /^\s*@@\s*(?:\(\s*)?["']?([^"'()\s][^"'()]*?)["']?(?:\s*\))?\s*$/;

const FENCE_OPEN = /^(\s*)(`{3,})\s*(\S*)\s*(.*)$/;

interface FenceArgs {
  render?: string;
  execute?: string;
  showCode: boolean;
  showHeader: boolean;
}

/** Parse the fence header args — the same --flag [value] grammar ExecutableCodeBlock.ParseArgs reads. */
function parseFenceArgs(rest: string): FenceArgs {
  const tokens = rest.split(/\s+/).filter(Boolean);
  const args: FenceArgs = { showCode: false, showHeader: false };
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (!token.startsWith("--")) continue;
    const name = token.slice(2);
    const value = i + 1 < tokens.length && !tokens[i + 1].startsWith("--") ? tokens[i + 1] : undefined;
    switch (name) {
      case "render":
        args.render = value;
        break;
      case "execute":
        args.execute = value ?? "";
        break;
      case "show-code":
        args.showCode = true;
        break;
      case "show-header":
        args.showHeader = true;
        break;
    }
  }
  return args;
}

/** Parse a `layout` fence body — YAML-ish address/area/id, or a legacy bare "{address}/{area}" path. */
function parseLayoutBlock(body: string): LayoutEmbedSegment | null {
  const fields = new Map<string, string>();
  for (const line of body.split("\n")) {
    const m = /^\s*(\w+)\s*:\s*(.+?)\s*$/.exec(line);
    if (m) fields.set(m[1].toLowerCase(), m[2]);
  }
  const address = fields.get("address");
  const area = fields.get("area");
  if (address && area) return { kind: "layout", address, area, id: fields.get("id") };
  const bare = body.trim();
  if (bare.includes("/") && !bare.includes("\n")) {
    // Legacy "{address}/{area}" — the last segment is the area.
    const slash = bare.lastIndexOf("/");
    return { kind: "layout", address: bare.slice(0, slash), area: bare.slice(slash + 1) };
  }
  return null;
}

let anonymousCellCounter = 0;

/**
 * Parse markdown into ordered interactive segments. Fenced blocks are extracted first (an @@
 * macro inside a fence stays literal); non-executable fences stay part of the surrounding
 * markdown text (react-markdown renders them as code).
 */
export function parseInteractiveMarkdown(text: string): InteractiveSegment[] {
  const segments: InteractiveSegment[] = [];
  let buffer: string[] = [];
  const flush = () => {
    const chunk = buffer.join("\n");
    if (chunk.trim().length > 0) segments.push({ kind: "markdown", text: chunk });
    buffer = [];
  };

  const lines = text.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const open = FENCE_OPEN.exec(lines[i]);
    if (!open) {
      const macro = AREA_MACRO_LINE.exec(lines[i]);
      if (macro) {
        flush();
        segments.push({ kind: "embed", path: macro[1].trim() });
      } else {
        buffer.push(lines[i]);
      }
      continue;
    }

    // A fence: collect to the closing fence (same or more backticks).
    const fenceMarker = open[2];
    const info = open[3] ?? "";
    const rest = open[4] ?? "";
    const body: string[] = [];
    let j = i + 1;
    for (; j < lines.length; j++) {
      const close = /^\s*(`{3,})\s*$/.exec(lines[j]);
      if (close && close[1].length >= fenceMarker.length) break;
      body.push(lines[j]);
    }

    const args = parseFenceArgs(rest);
    if (info === "mermaid") {
      flush();
      segments.push({ kind: "mermaid", code: body.join("\n") });
    } else if (info === "layout") {
      const layout = parseLayoutBlock(body.join("\n"));
      flush();
      if (layout) segments.push(layout);
      // an unparseable layout block degrades to nothing visible (the Blazor renderer shows an error)
    } else if (args.render !== undefined || args.execute !== undefined) {
      flush();
      segments.push({
        kind: "cell",
        language: info || "csharp",
        code: body.join("\n"),
        submissionId: args.render || args.execute || `cell-${++anonymousCellCounter}`,
        hasOutput: args.render !== undefined,
        showCode: args.showCode,
        showHeader: args.showHeader,
        header: lines[i].trim(),
      });
    } else {
      // Plain fence — stays in the markdown text (react-markdown renders the code block).
      buffer.push(lines[i], ...body);
      if (j < lines.length) buffer.push(lines[j]);
      i = j;
      continue;
    }
    i = j; // skip past the closing fence
  }
  flush();
  return segments;
}

/** The kernel submissions for a parsed document, in document order (the kernel builds state cell
 *  by cell, so order is load-bearing — MarkdownViewLogic.SubmitNext posts sequentially). */
export function cellSubmissions(segments: InteractiveSegment[]): MarkdownCellSubmission[] {
  return segments
    .filter((s): s is CodeCellSegment => s.kind === "cell")
    .map((s) => ({ code: s.code, id: s.submissionId, language: s.language }));
}

/** One kernel code submission (the wire SubmitCodeRequest: { code, id, language? }). */
export interface MarkdownCellSubmission {
  code: string;
  id: string;
  language?: string;
}

/** A live per-view markdown kernel — the viewer-owned Activity hosting the result areas. */
export interface MarkdownKernelSession {
  /** The kernel activity's address — the result areas render at (kernelAddress, cell id). */
  kernelAddress: string;
  /** Re-submit one cell (the Run affordance) — the result area updates in place. */
  submit(cell: MarkdownCellSubmission): void;
  /**
   * Release the per-view kernel when the view unmounts — the control-plane cancel
   * (requestedStatus: Cancelled on the activity node, the hub.CancelActivity twin). Without it
   * every doc-page visit LEAKS a live kernel activity hub on the portal. Optional so in-memory
   * test sessions need not implement it.
   */
  dispose?(): void;
}

/** The server-rendered document: the Markdig pipeline's HTML + the ordered kernel submissions
 *  (MeshOperations.RenderMarkdown / POST /api/mesh/render-markdown). */
export interface RenderedMarkdown {
  html: string;
  codeSubmissions: MarkdownCellSubmission[];
}

// ---- server-rendered HTML hydration ---------------------------------------------------------------
// The Markdig pipeline (the ONE parser — ExecutableCodeBlockRenderer + LayoutAreaMarkdownRenderer)
// emits interactive markers as SINGLE-LINE divs, pinned here verbatim:
//   <div class='layout-area' [data-raw-path='…'] [data-address='…' data-area='…' data-area-id='…']></div>
//   … with data-address='__KERNEL_ADDRESS__' for an executable block's result area;
//   <div class="md-code-cell-toolbar" data-submission-id="…" data-language="…"></div>  (the Run slot)
//   <div class='mermaid'>escaped source</div>
// The client renders the HTML chunks verbatim and mounts live views at the marker positions —
// the same split the native MAUI pack uses (MarkdownViewLogic.SplitRenderedHtml).

export const KERNEL_ADDRESS_PLACEHOLDER = "__KERNEL_ADDRESS__";

export interface HtmlChunkSegment {
  kind: "html";
  html: string;
}

export interface HtmlAreaSegment {
  kind: "area";
  /** The raw UCR path (@@-macro form) when the div carries only data-raw-path. */
  rawPath?: string;
  address?: string;
  area?: string;
  id?: string;
  /** True when the address is the kernel placeholder — the embed waits for the kernel session. */
  isKernel: boolean;
}

export interface HtmlToolbarSegment {
  kind: "toolbar";
  submissionId: string;
  language: string;
}

export interface HtmlMermaidSegment {
  kind: "mermaidHtml";
  code: string;
}

export type RenderedHtmlSegment = HtmlChunkSegment | HtmlAreaSegment | HtmlToolbarSegment | HtmlMermaidSegment;

const HTML_MARKER =
  /<div class=['"]layout-area['"]([^>]*)><\/div>|<div class=['"]md-code-cell-toolbar['"]([^>]*)><\/div>|<div class=['"]mermaid['"]>([\s\S]*?)<\/div>/g;

function attr(attrs: string, name: string): string | undefined {
  const m = new RegExp(`data-${name}=['"]([^'"]*)['"]`).exec(attrs);
  return m ? decodeHtmlAttribute(m[1]) : undefined;
}

function decodeHtmlAttribute(value: string): string {
  return value
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&amp;/g, "&");
}

/** Split server-rendered markdown HTML into chunks + interactive markers (pure; pinned by tests). */
export function splitRenderedHtml(html: string): RenderedHtmlSegment[] {
  const segments: RenderedHtmlSegment[] = [];
  let last = 0;
  for (const match of html.matchAll(HTML_MARKER)) {
    const index = match.index ?? 0;
    if (index > last) segments.push({ kind: "html", html: html.slice(last, index) });
    last = index + match[0].length;
    if (match[1] !== undefined) {
      const address = attr(match[1], "address");
      segments.push({
        kind: "area",
        rawPath: attr(match[1], "raw-path"),
        address,
        area: attr(match[1], "area"),
        id: attr(match[1], "area-id") || undefined,
        isKernel: address === KERNEL_ADDRESS_PLACEHOLDER,
      });
    } else if (match[2] !== undefined) {
      segments.push({
        kind: "toolbar",
        submissionId: attr(match[2], "submission-id") ?? "",
        language: attr(match[2], "language") ?? "csharp",
      });
    } else {
      segments.push({ kind: "mermaidHtml", code: decodeHtmlAttribute(match[3] ?? "").trim() });
    }
  }
  if (last < html.length) segments.push({ kind: "html", html: html.slice(last) });
  return segments;
}
