// React Native device HTML rendering — react-native-render-html turns server-prerendered HTML (doc
// bodies, tables, links, styled spans) into native components. Bundled ONLY on a device: metro resolves
// this .native file, while web/tsc/vitest use nativeHtml.tsx (no render-html dep). Same split as nativeFetch.
import { useMemo } from "react";
import RenderHtml from "react-native-render-html";
import { SvgUri } from "react-native-svg";
import { View, Image, useWindowDimensions, Platform, StyleSheet } from "react-native";
import { currentInstance } from "./connection";

export function NativeHtml({ html }: { html: string }) {
  const { width } = useWindowDimensions();
  // Strip the inline style off HEADINGS and DIVS (keep <span> colors). Memoized on `html` — NativeHtml
  // re-renders on width changes/parent renders, and this shouldn't re-run the regex each time.
  //  • headings — `<h1 style="font-size:2rem; line-height:1.15; letter-spacing:-0.02em">` makes render-html
  //    lay out a too-tight bold run that clips the first glyph (the "D" in "Doc").
  //  • divs — the doc header is a web "card" (`<div style="background:linear-gradient; padding:40px;
  //    margin:30px; min-height…">`) whose gradient/icon don't render natively but whose padding/height
  //    still reserve a big empty band after the title. Dropping div styles collapses that dead space.
  const cleaned = useMemo(() => (html || "").replace(/(<(?:h[1-6]|div)\b[^>]*?)\sstyle="[^"]*"/gi, "$1"), [html]);
  return (
    <View style={{ paddingLeft: 2 }}>
      <RenderHtml
        source={{ html: cleaned }}
        contentWidth={Math.max(240, (width || 360) - 96)}
        baseStyle={BASE}
        tagsStyles={TAGS}
        systemFonts={SYSTEM_FONTS}
        defaultTextProps={{ selectable: true }}
        renderers={RENDERERS}
        // Inline <svg>/<picture>/<source> aren't handled; <img> is (see RENDERERS.img).
        ignoredDomTags={IGNORED_TAGS}
      />
    </View>
  );
}

// Resolve a relative asset URL (e.g. "/static/NodeTypeIcons/box.svg") against the connected mesh so the
// device can fetch it; the local monolith (Memex.LocalMesh) serves /static/{collection}/{file}.
function resolveUrl(src: string): string {
  if (!src || /^https?:\/\//i.test(src) || src.startsWith("data:")) return src;
  const base = (currentInstance().url || "").replace(/\/+$/, "");
  return base ? `${base}/${src.replace(/^\/+/, "")}` : src;
}

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
