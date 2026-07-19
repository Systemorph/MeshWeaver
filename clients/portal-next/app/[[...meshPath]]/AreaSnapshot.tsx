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
  fetchAreaTarget,
  fetchNodeSnapshot,
  fetchRenderedArea,
  mintToken,
  resolvePortalOrigin,
  type AreaTarget,
  type RenderedAreaResult,
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
  // The HOME route (no explicit path) renders the signed-in user's Activity dashboard — the same
  // explicit `Address={userId} Area="Activity"` the Blazor Index.razor binds (the node's DEFAULT
  // area is the generic overview, not the dashboard). Explicit paths resolve into (node address,
  // area remainder); that resolution and the rendered snapshot fetch in parallel — render-area
  // does its own resolution internally, so neither depends on the other.
  const none: RenderedAreaResult = { kind: "none" };
  const [target, rendered]: [AreaTarget, RenderedAreaResult] =
    !mint || !resolvedPath
      ? [{ address: resolvedPath, area: "", id: "", redirectOnDenied: null }, none]
      : !path
        ? await Promise.all([
            Promise.resolve<AreaTarget>({ address: resolvedPath, area: "Activity", id: "", redirectOnDenied: null }),
            fetchRenderedArea(origin, mint.rawToken, resolvedPath, "Activity"),
          ])
        : await Promise.all([
            fetchAreaTarget(origin, mint.rawToken, resolvedPath),
            fetchRenderedArea(origin, mint.rawToken, resolvedPath),
          ]);

  const tree = rendered.kind === "ok" ? rendered.tree : null;
  const snapshot =
    !tree && mint && resolvedPath ? await fetchNodeSnapshot(origin, mint.rawToken, resolvedPath) : null;

  return (
    <LiveArea
      path={resolvedPath}
      target={target}
      initialTree={tree ?? (snapshot ? buildInitialTree(snapshot) : null)}
      // A rendered frame roots at the requested area (an explicit-area URL roots at its name;
      // the default-area subscribe at ""); the synthesized preview tree always roots at "".
      initialRootArea={tree ? target.area : ""}
      unauthenticated={!mint}
      // Server-detected RLS denial (authenticated visitor lacks Read) → the client redirects to the
      // node's public cover / paywall when the policy safely configures one — the same "no access ⇒
      // redirect here" the Blazor NamedAreaView does. The loop-guard + navigation live in LiveArea.
      initialDenied={rendered.kind === "denied"}
    />
  );
}
