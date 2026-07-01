// Async server component — ONE bounded snapshot round-trip per request, then done:
//   1. forward the incoming request's cookies to POST {portal}/api/tokens (short-lived token,
//      request-scoped, never persisted, never sent to the client);
//   2. resolve the mesh path ("" → the user's home partition, from the mint's nodePath);
//   3. fetch the node snapshot over REST (POST /api/mesh/get) and synthesize the SSR preview tree.
// The result is passed as plain JSON props into the <LiveArea> client boundary, whose initial
// render Next streams as HTML inside the page's Suspense boundary. No gRPC, no streams, no state.

import { cookies, headers } from "next/headers";
import {
  buildInitialTree,
  fetchNodeSnapshot,
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
  const snapshot = mint && resolvedPath ? await fetchNodeSnapshot(origin, mint.rawToken, resolvedPath) : null;

  return (
    <LiveArea
      path={resolvedPath}
      initialTree={snapshot ? buildInitialTree(snapshot) : null}
      unauthenticated={!mint}
    />
  );
}
