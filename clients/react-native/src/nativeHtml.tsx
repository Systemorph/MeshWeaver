// Web + test/fallback HTML rendering. On web (react-native-web) inject the HTML into a real DOM node;
// elsewhere without a DOM or the native renderer (the vitest mock) strip tags to readable text. A device
// build resolves nativeHtml.native.tsx instead (react-native-render-html) — the SAME platform-file split
// as nativeFetch.*, so the render-html dep is bundled ONLY on a device; tsc/vitest/web never load it.
import { createElement } from "react";
import { Text, Platform, StyleSheet } from "react-native";

export function NativeHtml({ html }: { html: string }) {
  if (Platform.OS === "web")
    return createElement("div", { className: "markdown-body", dangerouslySetInnerHTML: { __html: html || "" } });
  return <Text style={styles.body}>{stripTags(html)}</Text>;
}

// Readable plaintext from HTML: turn block-closers into newlines, drop the rest of the tags, unescape the
// common entities. Good enough for the no-DOM fallback (tests) — a device gets full rendering.
function stripTags(h: string): string {
  return (h || "")
    .replace(/<\/(p|div|h[1-6]|li|tr|table)>/gi, "\n")
    .replace(/<br\s*\/?>/gi, "\n")
    .replace(/<[^>]+>/g, " ")
    .replace(/&nbsp;/gi, " ")
    .replace(/&amp;/gi, "&")
    .replace(/&lt;/gi, "<")
    .replace(/&gt;/gi, ">")
    .replace(/[ \t]+/g, " ")
    .replace(/ *\n */g, "\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

const styles = StyleSheet.create({
  body: { fontSize: 15, color: "#242424", lineHeight: 22 },
});
