// React Native device HTML rendering — react-native-render-html turns server-prerendered HTML (doc
// bodies, tables, links, styled spans) into native components. Bundled ONLY on a device: metro resolves
// this .native file, while web/tsc/vitest use nativeHtml.tsx (no render-html dep). Same split as nativeFetch.
import RenderHtml from "react-native-render-html";
import { useWindowDimensions, Platform, StyleSheet } from "react-native";

export function NativeHtml({ html }: { html: string }) {
  const { width } = useWindowDimensions();
  return (
    <RenderHtml
      source={{ html: html || "" }}
      contentWidth={Math.max(240, (width || 360) - 88)}
      baseStyle={BASE}
      tagsStyles={TAGS}
      systemFonts={SYSTEM_FONTS}
      defaultTextProps={{ selectable: true }}
      // RN's Image can't load SVGs or the doc's relative/about: URLs (that crashed the render). Drop
      // images + inline SVG so the text/tables/links render; native icon rendering is a follow-up.
      ignoredDomTags={IGNORED_TAGS}
    />
  );
}

const IGNORED_TAGS = ["img", "svg", "picture", "source"];
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
