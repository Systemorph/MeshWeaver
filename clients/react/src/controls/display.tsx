import { useEffect, useMemo, useState, type ReactNode } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Badge, Text, Tooltip, MessageBar, MessageBarBody, MessageBarTitle } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import { ScopeProvider, useAreaState, useResolve } from "../area/context.js";
import { RenderArea } from "../render/ControlRenderer.js";
import { useAreaSourceFactory, type EmbeddedAreaHandle } from "../render/embeddedArea.js";
import { controlStyle } from "../render/style.js";
import { str, useClick, useText } from "./common.js";
import { resolveIconByName } from "./icon.js";

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

function IconView({ control }: { control: UiControl }): ReactNode {
  const name = useText(control.data);
  const onClick = useClick(control);
  const Cmp = resolveIconByName(name);
  if (Cmp) return <Cmp onClick={onClick} style={{ cursor: onClick ? "pointer" : undefined }} />;
  // URL or unknown name → render as image / text fallback
  if (/^https?:|^data:|\.(svg|png|jpg|jpeg|gif|webp)$/i.test(name))
    return <img src={name} alt="" width={20} height={20} onClick={onClick} />;
  return <span onClick={onClick}>{name}</span>;
}

function HtmlView({ control }: { control: UiControl }): ReactNode {
  return <div style={controlStyle(control)} dangerouslySetInnerHTML={{ __html: useText(control.data) }} />;
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

function MarkdownAreaEmbed({ path, nodePath }: { path: string; nodePath: string }): ReactNode {
  const factory = useAreaSourceFactory();
  const isArea = path.startsWith("area/");
  const address = isArea ? nodePath : path;
  const area = isArea ? path.slice("area/".length) : "";

  const [handle, setHandle] = useState<EmbeddedAreaHandle | null>(null);
  useEffect(() => {
    if (!factory || !address) return;
    const h = factory(address, { area: area || undefined });
    setHandle(h);
    return () => {
      h?.dispose?.();
      setHandle(null);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [factory, address, area]);

  if (!factory || !address || !handle) return null;
  const rootArea = handle.rootArea ?? area;
  return (
    <ScopeProvider source={handle.source} area={rootArea}>
      <EmbeddedMacroBody rootArea={rootArea} />
    </ScopeProvider>
  );
}

function MarkdownView({ control }: { control: UiControl }): ReactNode {
  // The wire property is `markdown` (MarkdownControl); `data` kept for literal/demo trees.
  const text = useText(control.markdown ?? control.data);
  const nodePath = str(useResolve(control.nodePath));
  const segments = useMemo(() => splitAreaMacros(text), [text]);
  return (
    <div className="mw-markdown" style={controlStyle(control)}>
      {segments.map((segment, i) =>
        segment.kind === "markdown" ? (
          <Markdown key={i} remarkPlugins={[remarkGfm]}>
            {segment.text}
          </Markdown>
        ) : (
          <MarkdownAreaEmbed key={`${segment.path}-${i}`} path={segment.path} nodePath={nodePath} />
        ),
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
