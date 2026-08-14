import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// EVERY BACKEND THAT SERVES A JS SHELL MUST SERVE THE VERBS THAT SHELL CALLS (issue #1474).
//
// There are two: the portal (`MeshApiEndpoints`) and the local sidecar (`Memex.LocalMesh`), and the
// React Native app talks to both. The sidecar mapped only `render-markdown`, so `content/list` and
// `upload` fell through its SPA fallback — which answers **200 with index.html**. The client then
// throws inside JSON.parse, and the file browser reports a parse error instead of "this backend does
// not serve that". A 404 would at least have been diagnosable; the fallback made it invisible.
//
// So the parity is asserted here rather than left to whoever notices: MESH_REST_PATHS is what the JS
// shells post to, and both endpoint maps must contain every one.
//
// 🚨 These are cross-tree reads: .github/workflows/clients.yml must be triggered by both files, or
// removing a verb server-side would not run this job. clients/react/src/ciTrigger.test.ts enforces it.

const pkgRoot = process.cwd();

/**
 * The `/api/mesh/*` verbs a JS shell reaches over HTTP because the gRPC message bus does not carry
 * them: a mesh QUERY is `IMeshQuery.Query`, a service call with no request type to post; content
 * listing and upload move bytes that are not mesh nodes; the markdown render is the one Markdig
 * pipeline. Every shell (portal, portal-next, react-native) posts to these.
 */
const MESH_REST_PATHS = {
  queryNodes: "/api/mesh/query-nodes",
  renderMarkdown: "/api/mesh/render-markdown",
  contentList: "/api/mesh/content/list",
  upload: "/api/mesh/upload",
};

const BACKENDS = {
  "the portal (MeshApiEndpoints)": "../../memex/Memex.Portal.Shared/Api/MeshApiEndpoints.cs",
  "the local sidecar (Memex.LocalMesh)": "../../memex/Memex.LocalMesh/LocalMeshApiEndpoints.cs",
};

/**
 * The route templates a C# endpoint map registers. Handles both shapes the sources use: an absolute
 * `MapPost("/api/mesh/upload", …)` and a `MapGroup(prefix)` + relative `MapPost("/upload", …)`.
 */
function mappedRoutes(source: string): string[] {
  const prefixes = [
    ...[...source.matchAll(/RoutePrefix\s*=\s*"([^"]+)"/g)].map((m) => m[1]),
    ...[...source.matchAll(/MapGroup\(\s*"([^"]+)"\s*\)/g)].map((m) => m[1]),
    "", // absolute MapPost paths need no prefix
  ];
  const routes: string[] = [];
  for (const m of source.matchAll(/\bMap(?:Post|Get)\(\s*"([^"]+)"/g))
    for (const prefix of prefixes) routes.push(`${prefix}${m[1]}`);
  return routes;
}

describe("both mesh backends serve every REST verb the client calls", () => {
  for (const [name, file] of Object.entries(BACKENDS)) {
    const source = readFileSync(resolve(pkgRoot, file), "utf8");
    const routes = mappedRoutes(source);

    it(`${name} — the route scrape found endpoints (a broken scrape must not pass vacuously)`, () => {
      expect(routes).toContain("/api/mesh/render-markdown");
    });

    for (const [verb, path] of Object.entries(MESH_REST_PATHS))
      it(`${name} maps ${verb} (${path})`, () => {
        expect(
          routes,
          `${path} is not mapped by ${file}. An unmapped /api/mesh/* route does not 404 there — it ` +
            `falls through to the SPA fallback and returns index.html with a 200, so the caller ` +
            `fails inside JSON.parse with nothing pointing at the missing endpoint.`,
        ).toContain(path);
      });
  }
});
