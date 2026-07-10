// Inject PWA <head> tags into the Expo web export's index.html.
//
// Expo SDK 52's Metro web (a non-expo-router app) doesn't generate a PWA manifest link or the
// Apple "add to home screen" meta from app.json, so we inject them after `expo export`. The manifest
// + icons ship via public/ (Expo copies public/ into the export root). Paths are RELATIVE so the app
// works whether it's served at a domain root or under a subpath. Run: node scripts/pwa-inject.mjs <index.html>
import { readFileSync, writeFileSync } from "node:fs";

const file = process.argv[2] || "dist/index.html";
const TAGS = `
    <link rel="manifest" href="manifest.json" />
    <link rel="apple-touch-icon" href="apple-touch-icon.png" />
    <link rel="icon" type="image/png" href="favicon.png" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
    <meta name="apple-mobile-web-app-title" content="MeshWeaver" />
    <meta name="theme-color" content="#2ea043" />
  `;

let html = readFileSync(file, "utf8");
if (html.includes('rel="manifest"')) {
  console.log("pwa-inject: already injected, skipping");
} else {
  html = html.replace("</head>", `${TAGS}</head>`);
  writeFileSync(file, html);
  console.log(`pwa-inject: injected PWA head tags into ${file}`);
}
