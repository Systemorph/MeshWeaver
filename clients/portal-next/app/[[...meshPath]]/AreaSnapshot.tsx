// Async server component — bounded snapshot round-trips per request, then done:
//   1. forward the incoming request's cookies to POST {portal}/api/mesh/whoami (who is viewing —
//      their mesh id is their home partition, i.e. the default route);
//   2. resolve the mesh path ("" → the viewer's home partition);
//   3. PRIMARY: fetch the node's rendered default area over REST (POST /api/mesh/render-area) —
//      the first Full {areas,data} frame of the same subscription the live client opens — for a
//      full-fidelity first paint;
//   4. FALLBACK (older portals without the verb / render timeout / denial): fetch the node
//      snapshot (POST /api/mesh/get) and synthesize the app-shell preview tree.
// Every call is cookie-authorized. 🚨 The server MINTS NO API TOKEN: it used to POST /api/tokens
// per render, and each mint writes two permanent ApiToken mesh nodes into the viewer's partition,
// so page traffic alone grew it without bound (issue #1477). SSR only ever reads.
// The result is passed as plain JSON props into the <LiveArea> client boundary, whose initial
// render Next streams as HTML inside the page's Suspense boundary. No gRPC, no streams, no state.

import { cookies, headers } from "next/headers";
import {
  buildInitialTree,
  fetchAreaTarget,
  fetchNodeSnapshot,
  fetchRenderedArea,
  fetchViewer,
  resolvePortalOrigin,
  type AreaTarget,
  type RenderedAreaResult,
} from "../../src/server/snapshot";
import { LiveArea } from "../../src/client/LiveArea";

export async function AreaSnapshot({ path }: { path: string }) {
  // Next 15: `headers()` / `cookies()` are async.
  const origin = resolvePortalOrigin(await headers());
  const cookieHeader = (await cookies())
    .getAll()
    .map((c) => `${c.name}=${c.value}`)
    .join("; ");

  const viewer = await fetchViewer(origin, cookieHeader);
  const resolvedPath = path || viewer?.userId || "";
  // The HOME route (no explicit path) renders the signed-in user's Activity dashboard — the same
  // explicit `Address={userId} Area="Activity"` the Blazor Index.razor binds (the node's DEFAULT
  // area is the generic overview, not the dashboard). Explicit paths resolve into (node address,
  // area remainder); that resolution and the rendered snapshot fetch in parallel — render-area
  // does its own resolution internally, so neither depends on the other.
  const none: RenderedAreaResult = { kind: "none" };
  const [target, rendered]: [AreaTarget, RenderedAreaResult] =
    !viewer || !resolvedPath
      ? [{ address: resolvedPath, area: "", id: "", redirectOnDenied: null }, none]
      : !path
        ? await Promise.all([
            Promise.resolve<AreaTarget>({ address: resolvedPath, area: "Activity", id: "", redirectOnDenied: null }),
            fetchRenderedArea(origin, cookieHeader, resolvedPath, "Activity"),
          ])
        : await Promise.all([
            fetchAreaTarget(origin, cookieHeader, resolvedPath),
            fetchRenderedArea(origin, cookieHeader, resolvedPath),
          ]);

  const tree = rendered.kind === "ok" ? rendered.tree : null;
  const snapshot =
    !tree && viewer && resolvedPath ? await fetchNodeSnapshot(origin, cookieHeader, resolvedPath) : null;

  return (
    <LiveArea
      path={resolvedPath}
      target={target}
      initialTree={tree ?? (snapshot ? buildInitialTree(snapshot) : null)}
      // A rendered frame roots at the requested area (an explicit-area URL roots at its name;
      // the default-area subscribe at ""); the synthesized preview tree always roots at "".
      initialRootArea={tree ? target.area : ""}
      unauthenticated={!viewer}
      // Server-detected RLS denial (authenticated visitor lacks Read) → the client redirects to the
      // node's public cover / paywall when the policy safely configures one — the same "no access ⇒
      // redirect here" the Blazor NamedAreaView does. The loop-guard + navigation live in LiveArea.
      initialDenied={rendered.kind === "denied"}
    />
  );
}
