import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Badge, Button, Text, Tooltip, MessageBar, MessageBarBody, MessageBarTitle } from "@fluentui/react-components";
import { Play16Regular } from "@fluentui/react-icons";
import type { UiControl } from "../area/types.js";
import { ScopeProvider, useAreaState, useResolve } from "../area/context.js";
import { useHtmlLinkInterceptor } from "../area/navigation.js";
import { RenderArea } from "../render/ControlRenderer.js";
import { useAreaSourceFactory, type EmbeddedAreaHandle } from "../render/embeddedArea.js";
import { useMeshOps } from "../live/meshOps.js";
import { controlStyle } from "../render/style.js";
import { str, useClick, useText } from "./common.js";
import { resolveIconByName } from "./icon.js";
import {
  cellSubmissions,
  parseInteractiveMarkdown,
  splitRenderedHtml,
  type CodeCellSegment,
  type InteractiveSegment,
  type MarkdownCellSubmission,
  type MarkdownKernelSession,
  type RenderedHtmlSegment,
  type RenderedMarkdown,
} from "./interactiveMarkdown.js";

function typo(t: string): { size: 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900; weight: "regular" | "semibold" | "bold" } {
  switch (String(t || "").toLowerCase()) {
    case "herotitle":
    case "pagetitle":
    case "h1":
      return { size: 800, weight: "bold" };
    case "header":
    case "h2":
      return { size: 700, weight: "bold" };
    case "paneheader":
    case "subject":
    case "h3":
      return { size: 500, weight: "semibold" };
    case "h4":
      return { size: 400, weight: "semibold" };
    case "h5":
    case "h6":
      return { size: 300, weight: "semibold" };
    default:
      return { size: 300, weight: "regular" };
  }
}

function Label({ control }: { control: UiControl }): ReactNode {
  const value = useText(control.data);
  const onClick = useClick(control);
  const t = typo(str(useResolve(control.typo)));
  return (
    <Text
      as="span"
      size={t.size}
      weight={(useResolve(control.weight) as any) ?? t.weight}
      onClick={onClick}
      style={{ cursor: onClick ? "pointer" : undefined, ...controlStyle(control) }}
    >
      {value}
    </Text>
  );
}

function BadgeView({ control }: { control: UiControl }): ReactNode {
  return <Badge appearance="filled" color="brand">{useText(control.data)}</Badge>;
}

/** True when the value is an inline SVG DOCUMENT (`<svg …>…</svg>`), the shape node Icons and the
 *  IconGenerator produce — as opposed to an icon NAME, a URL, or a data URI. */
export function isInlineSvg(value: string): boolean {
  return /^\s*<svg[\s>]/i.test(value);
}

/**
 * Defense-in-depth for inline-SVG injection: node Icons come from the (trusted) IconGenerator, but
 * an Icon field CAN be user-authored, and SVG supports active content. Strip the obvious vectors —
 * `<script>`/`<foreignObject>` elements, `on*` event handlers, and `javascript:` hrefs — before
 * injecting. (The Blazor side trusts the same field via MarkupString; this is a strictly safer
 * mirror, not a full sanitizer.)
 */
export function sanitizeInlineSvg(svg: string): string {
  return svg
    .replace(/<script[\s\S]*?<\/script\s*>/gi, "")
    .replace(/<foreignObject[\s\S]*?<\/foreignObject\s*>/gi, "")
    .replace(/\son\w+\s*=\s*"[^"]*"/gi, "")
    .replace(/\son\w+\s*=\s*'[^']*'/gi, "")
    .replace(/\son\w+\s*=\s*[^\s>]+/gi, "")
    .replace(/((?:xlink:)?href)\s*=\s*(["'])\s*javascript:[^"']*\2/gi, "");
}

function IconView({ control }: { control: UiControl }): ReactNode {
  const name = useText(control.data);
  const onClick = useClick(control);
  const cursor = onClick ? "pointer" : undefined;
  // Inline SVG markup (node Icons, generated avatars) must render as an SVG element — NOT as
  // escaped text (the "SVGs are not displayed" bug: `<span>{"<svg …>"}</span>` shows the source).
  if (isInlineSvg(name))
    return (
      <span
        onClick={onClick}
        style={{ cursor, display: "inline-flex", width: 20, height: 20 }}
        className="mw-inline-svg"
        dangerouslySetInnerHTML={{ __html: sanitizeInlineSvg(name) }}
      />
    );
  const Cmp = resolveIconByName(name);
  if (Cmp) return <Cmp onClick={onClick} style={{ cursor }} />;
  // URL or data-URI → image; anything else → text.
  if (/^https?:|^data:|\.(svg|png|jpg|jpeg|gif|webp)$/i.test(name))
    return <img src={name} alt="" width={20} height={20} onClick={onClick} />;
  return <span onClick={onClick}>{name}</span>;
}

function HtmlView({ control }: { control: UiControl }): ReactNode {
  // Injected anchors are not React elements — route their internal hrefs through the host.
  const onLinkClick = useHtmlLinkInterceptor();
  return <div style={controlStyle(control)} onClick={onLinkClick} dangerouslySetInnerHTML={{ __html: useText(control.data) }} />;
}

// ---- Markdown (with @@("…") layout-area embeds) ---------------------------------------------------
// MarkdownControl wire (src/MeshWeaver.Layout/MarkdownControl.cs): { markdown, nodePath?, html? }.
// The Blazor renderer resolves block-level @@("area/X") / @@("path") macros
// (MeshWeaver.Markdown/LayoutAreaMarkdownParser) into embedded layout areas — the mechanism the
// user home dashboard (Composer/Pinned/Threads/Catalog regions) and doc embeds are built from.
// This is the React mirror: the text splits at macro lines; each macro opens a nested area through
// the host's AreaSourceFactory (same machinery as LayoutAreaControl). "area/X" renders area X of
// the OWNING node (control.nodePath — the resolution base the server stamps); any other path
// renders that node's default area. Hosts without a factory render the text segments only.

type MarkdownSegment = { kind: "markdown"; text: string } | { kind: "embed"; path: string };

/** Block-level layout-area macro: @@("area/X"), @@("path"), @@"path", @@path — alone on a line. */
const AREA_MACRO_LINE = /^\s*@@\s*(?:\(\s*)?["']?([^"'()\s][^"'()]*?)["']?(?:\s*\))?\s*$/;

/** Split markdown into text segments and @@-embed macros (exported for tests). */
export function splitAreaMacros(text: string): MarkdownSegment[] {
  const segments: MarkdownSegment[] = [];
  let buffer: string[] = [];
  const flush = () => {
    const chunk = buffer.join("\n");
    if (chunk.trim().length > 0) segments.push({ kind: "markdown", text: chunk });
    buffer = [];
  };
  for (const line of text.split("\n")) {
    const match = AREA_MACRO_LINE.exec(line);
    if (match) {
      flush();
      segments.push({ kind: "embed", path: match[1].trim() });
    } else {
      buffer.push(line);
    }
  }
  flush();
  return segments;
}

/** Renders inside the nested source's scope once its root area arrives (regions collapse until then). */
function EmbeddedMacroBody({ rootArea }: { rootArea: string }): ReactNode {
  const state = useAreaState();
  if (state.areas?.[rootArea] == null) return null;
  return <RenderArea areaKey={rootArea} />;
}

/** Embed an area of an explicit (address, area, id) — the shared leaf under the @@-macro, the
 *  `layout` fence, the kernel result-area embeds, and per-item ItemArea cards (MeshSearch). */
export function AddressAreaEmbed({ address, area, id }: { address: string; area: string; id?: string }): ReactNode {
  const factory = useAreaSourceFactory();
  const [handle, setHandle] = useState<EmbeddedAreaHandle | null>(null);
  useEffect(() => {
    if (!factory || !address) return;
    const h = factory(address, { area: area || undefined, id });
    setHandle(h);
    return () => {
      h?.dispose?.();
      setHandle(null);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [factory, address, area, id]);

  if (!factory || !address || !handle) return null;
  const rootArea = handle.rootArea ?? area;
  return (
    <ScopeProvider source={handle.source} area={rootArea}>
      <EmbeddedMacroBody rootArea={rootArea} />
    </ScopeProvider>
  );
}

function MarkdownAreaEmbed({ path, nodePath }: { path: string; nodePath: string }): ReactNode {
  const isArea = path.startsWith("area/");
  const address = isArea ? nodePath : path;
  const area = isArea ? path.slice("area/".length) : "";
  return <AddressAreaEmbed address={address} area={area} />;
}

// ---- interactive code cells (the executable-markdown kernel) --------------------------------------

type KernelState =
  | { status: "none" }
  | { status: "unavailable" }
  | { status: "starting" }
  | { status: "ready"; session: MarkdownKernelSession }
  | { status: "error"; message: string };

/**
 * Boot the per-view markdown kernel when the document carries executable cells — the client twin
 * of the Blazor MarkdownView's OnAfterRenderAsync bootstrap: ONE kernel per view, created through
 * the host's MeshOps.startMarkdownKernel (viewer-owned Activity; routable before any result-area
 * subscription — the subscribe-before-create storm guard lives in the host implementation).
 */
function useMarkdownKernelForCells(cells: MarkdownCellSubmission[]): KernelState {
  const ops = useMeshOps();
  const [state, setState] = useState<KernelState>({ status: "none" });
  const startedFor = useRef<string | null>(null);
  const liveSession = useRef<MarkdownKernelSession | null>(null);

  const cellsKey = useMemo(() => cells.map((c) => c.id).join("|"), [cells]);
  useEffect(() => {
    if (cells.length === 0) {
      setState({ status: "none" });
      return;
    }
    if (!ops?.startMarkdownKernel) {
      setState({ status: "unavailable" });
      return;
    }
    if (startedFor.current === cellsKey) return; // one kernel per document content
    startedFor.current = cellsKey;
    setState({ status: "starting" });
    let live = true;
    ops.startMarkdownKernel(cells).then(
      (session) => {
        if (live) {
          liveSession.current = session;
          setState({ status: "ready", session });
        } else {
          // The view is already gone — release the just-arrived kernel instead of leaking it.
          session.dispose?.();
        }
      },
      (err) => {
        if (live) setState({ status: "error", message: err instanceof Error ? err.message : String(err) });
      },
    );
    return () => {
      live = false;
      // Unmount / document change RELEASES the per-view kernel (the Blazor view-dispose twin) —
      // without this every doc-page visit leaks a live kernel activity hub on the portal.
      liveSession.current?.dispose?.();
      liveSession.current = null;
      startedFor.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, cellsKey]);

  return state;
}

function useMarkdownKernel(segments: InteractiveSegment[]): KernelState {
  const cells = useMemo(() => cellSubmissions(segments), [segments]);
  return useMarkdownKernelForCells(cells);
}

/** The static kernel notices — the exact Blazor parity texts (MarkdownViewLogic). */
function KernelNotice({ text }: { text: string }): ReactNode {
  return (
    <div
      style={{
        border: "1px solid var(--colorNeutralStroke2)",
        background: "var(--colorNeutralBackground3)",
        color: "var(--colorNeutralForeground3)",
        padding: "8px 12px",
        borderRadius: 4,
        margin: "8px 0",
        fontSize: 13,
      }}
    >
      {text}
    </div>
  );
}

/**
 * One executable code cell — the React twin of the Blazor notebook-cell frame
 * (ExecutableCodeBlockRenderer.CellClass): optional code display per --show-header/--show-code,
 * a Run affordance once the kernel is live, and the kernel result area embedded beneath.
 */
function MarkdownCodeCell({ cell, kernel }: { cell: CodeCellSegment; kernel: KernelState }): ReactNode {
  const showsCode = cell.showCode || cell.showHeader;
  const codeText = cell.showHeader ? `${cell.header}\n${cell.code}` : cell.code;

  const output =
    !cell.hasOutput ? null
    : kernel.status === "ready" ? (
        <AddressAreaEmbed address={kernel.session.kernelAddress} area={cell.submissionId} />
      )
    : kernel.status === "starting" ? <KernelNotice text="Starting interactive kernel…" />
    : kernel.status === "error" ? <KernelNotice text={`Interactive kernel failed to start: ${kernel.message}`} />
    : <KernelNotice text="Interactive code execution is unavailable here — this view has no owning node to host the kernel." />;

  if (!showsCode) return <div className="md-code-cell-output">{output}</div>;

  return (
    <div
      className="md-code-cell"
      data-mw-code-cell={cell.submissionId}
      style={{
        border: "1px solid var(--colorNeutralStroke2)",
        borderRadius: 6,
        margin: "8px 0",
        overflow: "hidden",
      }}
    >
      <div
        className="md-code-cell-toolbar"
        style={{
          display: "flex",
          alignItems: "center",
          gap: 8,
          padding: "2px 8px",
          background: "var(--colorNeutralBackground3)",
          borderBottom: "1px solid var(--colorNeutralStroke2)",
        }}
      >
        <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
          {cell.language}
        </Text>
        <div style={{ flex: 1 }} />
        {kernel.status === "ready" && cell.hasOutput ? (
          <Button
            appearance="subtle"
            size="small"
            icon={<Play16Regular />}
            title="Run"
            aria-label="Run"
            onClick={() => kernel.session.submit({ code: cell.code, id: cell.submissionId, language: cell.language })}
          />
        ) : null}
      </div>
      <pre
        style={{
          margin: 0,
          padding: 12,
          overflow: "auto",
          fontFamily: "var(--fontFamilyMonospace)",
          fontSize: 13,
          background: "var(--colorNeutralBackground2)",
        }}
      >
        <code>{codeText}</code>
      </pre>
      {output ? (
        <div className="md-code-cell-output" style={{ borderTop: "1px solid var(--colorNeutralStroke2)", padding: 8 }}>
          {output}
        </div>
      ) : null}
    </div>
  );
}

/** Mermaid diagram — lazily imports the mermaid package (a host dependency, like Monaco); until it
 *  arrives (or in hosts without it) the diagram source shows as a code block. */
function MermaidView({ code }: { code: string }): ReactNode {
  const [svg, setSvg] = useState<string | null>(null);
  const idRef = useRef(`mermaid-${Math.random().toString(36).slice(2)}`);
  useEffect(() => {
    let live = true;
    import("mermaid")
      .then(async (m) => {
        const mermaid = (m as { default?: unknown }).default ?? m;
        const api = mermaid as {
          initialize(config: Record<string, unknown>): void;
          render(id: string, code: string): Promise<{ svg: string }>;
        };
        api.initialize({ startOnLoad: false, securityLevel: "loose" });
        const rendered = await api.render(idRef.current, code);
        if (live) setSvg(rendered.svg);
      })
      .catch(() => undefined); // package absent / parse error — the code fallback stays
    return () => {
      live = false;
    };
  }, [code]);
  if (svg) return <div className="mw-mermaid" dangerouslySetInnerHTML={{ __html: svg }} />;
  return (
    <pre style={{ background: "var(--colorNeutralBackground3)", padding: 12, borderRadius: 4, overflow: "auto" }}>
      <code>{code}</code>
    </pre>
  );
}

/** Render the parsed interactive segments — the CLIENT-parsed fallback for hosts without the
 *  server-side Markdig render (static demos, unit tests). */
function InteractiveSegments({ segments, nodePath }: { segments: InteractiveSegment[]; nodePath: string }): ReactNode {
  const kernel = useMarkdownKernel(segments);
  return (
    <>
      {segments.map((segment, i) => {
        switch (segment.kind) {
          case "markdown":
            return (
              <Markdown key={i} remarkPlugins={[remarkGfm]}>
                {segment.text}
              </Markdown>
            );
          case "embed":
            return <MarkdownAreaEmbed key={`${segment.path}-${i}`} path={segment.path} nodePath={nodePath} />;
          case "layout":
            return (
              <AddressAreaEmbed key={`${segment.address}-${segment.area}-${i}`} address={segment.address} area={segment.area} id={segment.id} />
            );
          case "mermaid":
            return <MermaidView key={i} code={segment.code} />;
          case "cell":
            return <MarkdownCodeCell key={`${segment.submissionId}-${i}`} cell={segment} kernel={kernel} />;
        }
      })}
    </>
  );
}

/**
 * Render SERVER-rendered (Markdig) HTML with the interactive markers hydrated — the primary path:
 * the client renders the pipeline's HTML verbatim and mounts live views at the marker positions
 * (the same split the native MAUI pack uses). Kernel result areas embed only once the kernel is
 * routable; toolbars become Run affordances.
 */
function RenderedMarkdownView({
  rendered,
  nodePath,
}: {
  rendered: RenderedMarkdown;
  nodePath: string;
}): ReactNode {
  const kernel = useMarkdownKernelForCells(rendered.codeSubmissions);
  const segments = useMemo(() => splitRenderedHtml(rendered.html), [rendered.html]);
  const submissionById = useMemo(
    () => new Map(rendered.codeSubmissions.map((s) => [s.id, s])),
    [rendered.codeSubmissions],
  );
  return (
    <>
      {segments.map((segment, i) => renderHtmlSegment(segment, i, nodePath, kernel, submissionById))}
    </>
  );
}

function renderHtmlSegment(
  segment: RenderedHtmlSegment,
  key: number,
  nodePath: string,
  kernel: KernelState,
  submissionById: Map<string, MarkdownCellSubmission>,
): ReactNode {
  switch (segment.kind) {
    case "html":
      return segment.html.trim().length === 0 ? null : (
        // The Markdig pipeline's own output — rendered verbatim, exactly like the Blazor view.
        <div key={key} dangerouslySetInnerHTML={{ __html: segment.html }} />
      );
    case "mermaidHtml":
      return <MermaidView key={key} code={segment.code} />;
    case "toolbar": {
      // The Run slot of a notebook cell (ExecutableCodeBlockRenderer.CellToolbarClass).
      const submission = submissionById.get(segment.submissionId);
      return (
        <div
          key={key}
          className="md-code-cell-toolbar"
          style={{ display: "flex", alignItems: "center", gap: 8, padding: "2px 8px" }}
        >
          <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
            {segment.language}
          </Text>
          <div style={{ flex: 1 }} />
          {kernel.status === "ready" && submission ? (
            <Button
              appearance="subtle"
              size="small"
              icon={<Play16Regular />}
              title="Run"
              aria-label="Run"
              onClick={() => kernel.session.submit(submission)}
            />
          ) : null}
        </div>
      );
    }
    case "area": {
      if (segment.isKernel) {
        // A kernel result area — embed only once the per-view activity is routable.
        if (kernel.status === "ready")
          return (
            <AddressAreaEmbed
              key={key}
              address={kernel.session.kernelAddress}
              area={segment.area ?? ""}
              id={segment.id}
            />
          );
        if (kernel.status === "starting") return <KernelNotice key={key} text="Starting interactive kernel…" />;
        if (kernel.status === "error")
          return <KernelNotice key={key} text={`Interactive kernel failed to start: ${kernel.message}`} />;
        return (
          <KernelNotice
            key={key}
            text="Interactive code execution is unavailable here — this view has no owning node to host the kernel."
          />
        );
      }
      if (segment.address)
        return <AddressAreaEmbed key={key} address={segment.address} area={segment.area ?? ""} id={segment.id} />;
      if (segment.rawPath) {
        // Raw @@-macro reference — "@/path" or "area/X" resolved against the authoring node.
        const raw = segment.rawPath.replace(/^@\//, "");
        return <MarkdownAreaEmbed key={key} path={raw} nodePath={nodePath} />;
      }
      return null;
    }
  }
}

/** Fetch the server-side Markdig render for raw markdown (memoized per text+nodePath). */
function useServerRenderedMarkdown(markdown: string, nodePath: string): RenderedMarkdown | null {
  const ops = useMeshOps();
  const [rendered, setRendered] = useState<RenderedMarkdown | null>(null);
  const requestedFor = useRef<string | null>(null);
  const inputKey = `${nodePath}\u0000${markdown}`;
  useEffect(() => {
    if (!ops?.renderMarkdown || markdown.trim().length === 0) return;
    if (requestedFor.current === inputKey) return;
    requestedFor.current = inputKey;
    let live = true;
    ops.renderMarkdown(markdown, nodePath || undefined).then(
      (r) => {
        if (live && r?.html) setRendered(r);
      },
      () => undefined, // render failure → the client-side fallback stays
    );
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, inputKey]);
  return rendered;
}

function MarkdownView({ control }: { control: UiControl }): ReactNode {
  // The wire property is `markdown` (MarkdownControl); `data` kept for literal/demo trees.
  const text = useText(control.markdown ?? control.data);
  const nodePath = str(useResolve(control.nodePath));
  // Pre-rendered HTML on the control wins; else the SERVER's Markdig render (the one parser);
  // else the plain client-side fallback (static demos / hosts without renderMarkdown).
  const controlHtml = str(useResolve(control.html));
  const serverRendered = useServerRenderedMarkdown(controlHtml ? "" : text, nodePath);
  const fallbackSegments = useMemo(
    () => (controlHtml || serverRendered ? [] : parseInteractiveMarkdown(text)),
    [controlHtml, serverRendered, text],
  );
  // Server-rendered anchors ("/Doc/…") are not React elements — route them through the host.
  const onLinkClick = useHtmlLinkInterceptor();
  return (
    <div className="mw-markdown" style={controlStyle(control)} onClick={onLinkClick}>
      {controlHtml ? (
        <RenderedMarkdownView rendered={{ html: controlHtml, codeSubmissions: [] }} nodePath={nodePath} />
      ) : serverRendered ? (
        <RenderedMarkdownView rendered={serverRendered} nodePath={nodePath} />
      ) : (
        <InteractiveSegments segments={fallbackSegments} nodePath={nodePath} />
      )}
    </div>
  );
}

/**
 * Strip CriticMarkup annotation markers for plain display — the read-only degradation of the
 * Blazor collaborative view's rich rendering: insertions keep their text, deletions drop,
 * substitutions keep the new text, and comment/highlight markers disappear. Exported for tests.
 */
export function stripAnnotations(text: string): string {
  return text
    .replace(/\{\+\+([\s\S]*?)\+\+\}/g, "$1") // insertion → keep
    .replace(/\{--[\s\S]*?--\}/g, "") // deletion → drop
    .replace(/\{~~([\s\S]*?)~>([\s\S]*?)~~\}/g, "$2") // substitution → the new text
    .replace(/\{==([\s\S]*?)==\}/g, "$1") // highlight → keep the text
    .replace(/\{>>[\s\S]*?<<\}/g, ""); // comment → hidden
}

/**
 * CollaborativeMarkdown — the READ-ONLY rendered overview of a markdown node
 * (CollaborativeMarkdownControl: "Used in the read-only overview of markdown nodes"). This is a
 * DISPLAY view: the annotated markdown renders as content (annotation markers degraded via
 * stripAnnotations), @@("area/…") embeds resolve — never an editor. Mapping this control to the
 * Monaco markdown editor was the "markdown pages come in edit mode" parity bug. Rich annotation
 * UI (accept/reject, comment threads) is a follow-up.
 */
export function CollaborativeMarkdownView({ control }: { control: UiControl }): ReactNode {
  const raw = useText(control.value ?? control.markdown ?? control.data);
  const nodePath = str(useResolve(control.nodePath));
  const display = useMemo(() => stripAnnotations(raw), [raw]);
  const serverRendered = useServerRenderedMarkdown(display, nodePath);
  const fallbackSegments = useMemo(
    () => (serverRendered ? [] : parseInteractiveMarkdown(display)),
    [serverRendered, display],
  );
  const onLinkClick = useHtmlLinkInterceptor();
  return (
    <div className="mw-markdown mw-collaborative-markdown" style={controlStyle(control)} onClick={onLinkClick}>
      {serverRendered ? (
        <RenderedMarkdownView rendered={serverRendered} nodePath={nodePath} />
      ) : (
        <InteractiveSegments segments={fallbackSegments} nodePath={nodePath} />
      )}
    </div>
  );
}

function CodeSample({ control }: { control: UiControl }): ReactNode {
  return (
    <pre
      style={{
        background: "var(--colorNeutralBackground3)",
        padding: 12,
        borderRadius: 4,
        overflow: "auto",
        fontFamily: "var(--fontFamilyMonospace)",
        fontSize: 13,
      }}
    >
      <code>{useText(control.data)}</code>
    </pre>
  );
}

function ExceptionView({ control }: { control: UiControl }): ReactNode {
  const message = useText(control.message);
  const type = useText(control.type);
  const stack = useText(control.stackTrace);
  return (
    <MessageBar intent="error">
      <MessageBarBody>
        <MessageBarTitle>{type || "Error"}</MessageBarTitle>
        {message}
        {stack ? <pre style={{ whiteSpace: "pre-wrap", fontSize: 12, marginTop: 4 }}>{stack}</pre> : null}
      </MessageBarBody>
    </MessageBar>
  );
}

function Spacer(): ReactNode {
  return <div style={{ flex: 1 }} />;
}

export const displayControls = {
  Label,
  Badge: BadgeView,
  Icon: IconView,
  Html: HtmlView,
  Markdown: MarkdownView,
  CodeSample,
  Highlight: CodeSample,
  Exception: ExceptionView,
  Spacer,
};

export { Tooltip };
