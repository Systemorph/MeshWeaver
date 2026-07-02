// Async server component — bounded snapshot round-trips per request, then done:
//   1. forward the incoming request's cookies to POST {portal}/api/tokens (short-lived token,
//      request-scoped, never persisted, never sent to the client);
//   2. resolve the mesh path ("" → the user's home partition, from the mint's nodePath);
//   3. PRIMARY: fetch the node's rendered default area over REST (POST /api/mesh/render-area) —
//      the first Full {areas,data} frame of the same subscription the live client opens — for a
//      full-fidelity first paint;
//   4. FALLBACK (older portals without the verb / render timeout / denial): fetch the node
//      snapshot (POST /api/mesh/get) and synthesize the app-shell preview tree.
// The result is passed as plain JSON props into the <LiveArea> client boundary, whose initial
// render Next streams as HTML inside the page's Suspense boundary. No gRPC, no streams, no state.

import { cookies, headers } from "next/headers";
import {
  buildInitialTree,
  fetchNodeSnapshot,
  fetchRenderedArea,
  mintToken,
  resolvePortalOrigin,
} from "../../src/server/snapshot";
import { LiveArea } from "../../src/client/LiveArea";

export async function AreaSnapshot({ path }: { path: string }) {
  const origin = resolvePortalOrigin(headers());
  const cookieHeader = cookies()
    .getAll()
    .map((c) => `${c.name}=${c.value}`)
    .join("; ");

  const mint = await mintToken(origin, cookieHeader);
  const resolvedPath = path || mint?.userId || "";
  const rendered = mint && resolvedPath ? await fetchRenderedArea(origin, mint.rawToken, resolvedPath) : null;
  const snapshot =
    !rendered && mint && resolvedPath ? await fetchNodeSnapshot(origin, mint.rawToken, resolvedPath) : null;

  return (
    <LiveArea
      path={resolvedPath}
      initialTree={rendered ?? (snapshot ? buildInitialTree(snapshot) : null)}
      unauthenticated={!mint}
    />
  );
}
