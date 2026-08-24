// React Native device HTML rendering — react-native-render-html turns server-prerendered HTML (doc
// bodies, tables, links, styled spans) into native components. Bundled ONLY on a device: metro resolves
// this .native file, while web/tsc/vitest use nativeHtml.tsx (no render-html dep). Same split as nativeFetch.
import { useMemo } from "react";
import RenderHtml from "react-native-render-html";
import { SvgUri, SvgXml } from "react-native-svg";
import { View, Image, useWindowDimensions, Platform, StyleSheet } from "react-native";
import { resolveAssetUrl } from "./connection";

/** Aspect ratio off an inline svg's viewBox/width/height, for sizing; 4:3 when undeclared. */
function svgAspect(markup: string): number {
  const vb = /viewBox\s*=\s*["']\s*[\d.eE+-]+[\s,]+[\d.eE+-]+[\s,]+([\d.eE+-]+)[\s,]+([\d.eE+-]+)/.exec(markup);
  if (vb) {
    const w = parseFloat(vb[1]);
    const h = parseFloat(vb[2]);
    if (w > 0 && h > 0) return w / h;
  }
  return 4 / 3;
}

export function NativeHtml({ html }: { html: string }) {
  const { width } = useWindowDimensions();
  // Inline <svg> blocks (the docs' colorful diagrams) render through react-native-svg — the HTML
  // renderer cannot draw them (they sit in IGNORED_TAGS), which silently DROPPED every diagram on
  // a device. Split them out and interleave: html chunks through RenderHtml, svg chunks through
  // SvgXml sized to the content width by their viewBox aspect. The non-greedy match is fine for
  // doc diagrams (top-level, un-nested); a nested <svg> would simply fall back to being dropped.
  const contentW = Math.max(240, (width || 360) - 96);
  const svgSplit = useMemo(() => (html || "").split(/(<svg[\s\S]*?<\/svg>)/gi), [html]);
  if (svgSplit.length > 1)
    return (
      <View style={{ paddingLeft: 2 }}>
        {svgSplit.map((chunk, i) =>
          /^<svg/i.test(chunk) ? (
            <SvgXml key={i} xml={chunk} width={contentW} height={Math.round(contentW / svgAspect(chunk))} />
          ) : chunk.trim() ? (
            <NativeHtmlChunk key={i} html={chunk} contentW={contentW} />
          ) : null,
        )}
      </View>
    );
  return (
    <View style={{ paddingLeft: 2 }}>
      <NativeHtmlChunk html={html} contentW={contentW} />
    </View>
  );
}

function NativeHtmlChunk({ html, contentW }: { html: string; contentW: number }) {
  // Strip the inline style off HEADINGS and DIVS (keep <span> colors). Memoized on `html` — NativeHtml
  // re-renders on width changes/parent renders, and this shouldn't re-run the regex each time.
  //  • headings — `<h1 style="font-size:2rem; line-height:1.15; letter-spacing:-0.02em">` makes render-html
  //    lay out a too-tight bold run that clips the first glyph (the "D" in "Doc").
  //  • divs — the doc header is a web "card" (`<div style="background:linear-gradient; padding:40px;
  //    margin:30px; min-height…">`) whose gradient/icon don't render natively but whose padding/height
  //    still reserve a big empty band after the title. Dropping div styles collapses that dead space.
  const cleaned = useMemo(() => (html || "").replace(/(<(?:h[1-6]|div)\b[^>]*?)\sstyle="[^"]*"/gi, "$1"), [html]);
  return (
      <RenderHtml
        source={{ html: cleaned }}
        contentWidth={contentW}
        baseStyle={BASE}
        tagsStyles={TAGS}
        systemFonts={SYSTEM_FONTS}
        defaultTextProps={{ selectable: true }}
        renderers={RENDERERS}
        // Standalone inline <svg> blocks are interleaved by NativeHtml above; a NESTED svg (inside
        // a paragraph) still lands here and is dropped — <picture>/<source> stay unhandled.
        ignoredDomTags={IGNORED_TAGS}
      />
  );
}

// Relative asset URLs (e.g. "/static/NodeTypeIcons/box.svg") resolve against the connected mesh —
// the ONE shared rule (connection.resolveAssetUrl), also used by the search tiles' ResultIcon.
const resolveUrl = resolveAssetUrl;

function px(tnode: any, attr: string, prop: string): number | undefined {
  const a = Number(tnode?.attributes?.[attr]);
  if (Number.isFinite(a) && a > 0) return a;
  const m = String(tnode?.attributes?.style ?? "").match(new RegExp(`${prop}\\s*:\\s*(\\d+(?:\\.\\d+)?)\\s*px`, "i"));
  return m ? Number(m[1]) : undefined;
}

// Custom <img>: RN's Image can't decode SVG, and relative/`about:` URLs crash the loader. Resolve the URL
// against the mesh, render SVGs via react-native-svg (SvgUri), raster via Image; skip anything unresolvable.
const RENDERERS = {
  img: ({ tnode }: any) => {
    const url = resolveUrl(String(tnode?.attributes?.src ?? ""));
    if (!/^https?:\/\//i.test(url)) return null;
    const w = px(tnode, "width", "width") ?? 40;
    const h = px(tnode, "height", "height") ?? w;
    return /\.svg(\?|$)/i.test(url)
      ? <SvgUri uri={url} width={w} height={h} />
      : <Image source={{ uri: url }} style={{ width: w, height: h }} resizeMode="contain" />;
  },
};

const IGNORED_TAGS = ["svg", "picture", "source"];
const SYSTEM_FONTS = Platform.OS === "ios" ? ["System", "Menlo"] : ["sans-serif", "monospace"];
const BASE = { color: "#242424", fontSize: 15, lineHeight: 22 } as any;
const TAGS = {
  h1: { fontSize: 24, fontWeight: "700", marginTop: 10, marginBottom: 8 },
  h2: { fontSize: 20, fontWeight: "700", marginTop: 10, marginBottom: 6 },
  h3: { fontSize: 17, fontWeight: "600", marginTop: 8, marginBottom: 4 },
  p: { marginTop: 0, marginBottom: 8 },
  a: { color: "#0f6cbd", textDecorationLine: "none" },
  code: { fontFamily: Platform.OS === "ios" ? "Menlo" : "monospace", fontSize: 13, backgroundColor: "#f2f2f2" },
  pre: { backgroundColor: "#f5f5f5", padding: 10, borderRadius: 6 },
  table: { borderWidth: StyleSheet.hairlineWidth, borderColor: "#ddd", marginBottom: 8 },
  th: { fontWeight: "700", padding: 6, backgroundColor: "#f7f7f7" },
  td: { padding: 6, borderTopWidth: StyleSheet.hairlineWidth, borderColor: "#eee" },
  ul: { marginBottom: 8 },
  li: { marginBottom: 4 },
} as any;
