// The one dynamic route: URL path → mesh path. `/next/Doc/GUI` renders the default layout area of
// mesh node "Doc/GUI"; the bare `/next` renders the authenticated user's home partition (resolved
// server-side from the token mint's nodePath — the SPA's no-hash default).
//
// Streaming SSR: the page returns instantly with the skeleton fallback; the async <AreaSnapshot>
// server component (token mint + REST snapshot per request — stateless, no server-held streams)
// streams its HTML into the boundary when the snapshot arrives.

import { Suspense } from "react";
import { meshPathFromSegments } from "../../src/meshPath";
import { AreaSnapshot } from "./AreaSnapshot";

// Every request depends on the caller's cookies (per-request token mint) — never prerender.
export const dynamic = "force-dynamic";

export default function MeshPage({ params }: { params: { meshPath?: string[] } }) {
  const path = meshPathFromSegments(params.meshPath);
  return (
    <Suspense fallback={<AreaSkeleton path={path} />}>
      <AreaSnapshot path={path} />
    </Suspense>
  );
}

/** Plain-HTML skeleton — part of the first flush, styled without Griffel so it needs no
 *  extraction and cannot block on anything. */
function AreaSkeleton({ path }: { path: string }) {
  return (
    <div data-mw-skeleton aria-busy="true" style={{ opacity: 0.6 }}>
      <div style={{ height: 32, width: 320, borderRadius: 6, background: "var(--colorNeutralBackground3, #ebebeb)" }} />
      <div style={{ height: 16, width: 200, borderRadius: 6, background: "var(--colorNeutralBackground3, #ebebeb)", marginTop: 12 }} />
      <div style={{ height: 120, borderRadius: 6, background: "var(--colorNeutralBackground3, #ebebeb)", marginTop: 20 }} />
      <p style={{ marginTop: 16, fontSize: 12 }}>Loading {path ? `“${path}”` : "your home"}…</p>
    </div>
  );
}
