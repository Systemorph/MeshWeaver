// Tiny zero-dep static server for the exported web build (dist/), with SPA fallback to index.html.
// Playwright's webServer starts this after `expo export --platform web`.
import http from "node:http";
import { readFile } from "node:fs/promises";
import { join, extname } from "node:path";
import { fileURLToPath } from "node:url";

const dist = fileURLToPath(new URL("../dist/", import.meta.url));
const port = Number(process.argv[2] || 8080);
const types = {
  ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript", ".json": "application/json",
  ".css": "text/css", ".map": "application/json", ".ttf": "font/ttf", ".woff": "font/woff",
  ".woff2": "font/woff2", ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon",
};

http.createServer(async (req, res) => {
  let p = decodeURIComponent((req.url || "/").split("?")[0]);
  if (p === "/" || p.endsWith("/")) p += "index.html";
  const send = (buf, path) =>
    res.writeHead(200, { "content-type": types[extname(path)] || "application/octet-stream" }).end(buf);
  try {
    send(await readFile(join(dist, p)), p);
  } catch {
    try {
      send(await readFile(join(dist, "index.html")), "index.html"); // SPA fallback
    } catch {
      res.writeHead(404).end("not found");
    }
  }
}).listen(port, () => console.log(`serving ${dist} on http://localhost:${port}`));
