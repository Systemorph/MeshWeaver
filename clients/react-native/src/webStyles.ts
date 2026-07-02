// Global web styling. react-native-web renders host <div>s, so the shell chrome is laid out with RN
// StyleSheet, but the CONTENT pane injects real HTML (marked → HTML) that needs a stylesheet — we reuse
// Blazor's github-markdown CSS verbatim (.markdown-body) for pixel parity, plus a few chrome details
// (system font, quiet scrollbars, link colour) that are awkward to express as inline RN styles.
import { Platform } from "react-native";
import { githubMarkdownCss } from "./githubMarkdownCss";
import { githubMarkdownDarkCss } from "./githubMarkdownDarkCss";

const CHROME_CSS = `
:root { --mw-accent: #0f6cbd; --mw-accent-hover: #115ea3; }
html, body, #root { height: 100%; width: 100%; margin: 0; background: #ffffff; }
[data-theme="dark"] html, [data-theme="dark"] body, html[data-theme="dark"], body[data-theme="dark"] { background: #1b1a19; }
/* react-native-web mounts the app in #root — make the flex chain fill the viewport both ways. */
#root { display: flex; }
#root > * { flex: 1 1 auto; min-width: 0; min-height: 0; }
body {
  font-family: "Segoe UI Variable", "Segoe UI", -apple-system, BlinkMacSystemFont, "Helvetica Neue", Arial, sans-serif;
  color: #242424;
  -webkit-font-smoothing: antialiased;
}
/* The content pane owns its own reading width + rhythm; keep the markdown flush to the pane. */
.markdown-body {
  font-family: "Segoe UI Variable", "Segoe UI", -apple-system, BlinkMacSystemFont, "Helvetica Neue", Arial, sans-serif;
  font-size: 15px;
  background: transparent;
}
.markdown-body a { color: var(--mw-accent); text-decoration: none; }
.markdown-body a:hover { text-decoration: underline; }
.markdown-body h1, .markdown-body h2 { border-bottom: 1px solid #eaecef; padding-bottom: .3em; }
.markdown-body table { display: table; width: 100%; }
/* Quiet macOS-style scrollbars. */
*::-webkit-scrollbar { width: 10px; height: 10px; }
*::-webkit-scrollbar-thumb { background: rgba(0,0,0,.18); border-radius: 6px; border: 2px solid transparent; background-clip: content-box; }
*::-webkit-scrollbar-thumb:hover { background: rgba(0,0,0,.3); background-clip: content-box; }
*::-webkit-scrollbar-track { background: transparent; }
`;

let injected = false;

/** Inject the content + chrome stylesheet once (web only; a no-op on native). */
export function ensureWebStyles(): void {
  if (injected || Platform.OS !== "web" || typeof document === "undefined") return;
  injected = true;
  const style = document.createElement("style");
  style.setAttribute("data-mw", "web-styles");
  style.textContent = githubMarkdownCss + "\n" + githubMarkdownDarkCss + "\n" + CHROME_CSS;
  document.head.appendChild(style);
}
