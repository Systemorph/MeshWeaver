// URL path <-> mesh path mapping for the [[...meshPath]] optional catch-all route.
//
// `/next/Doc/GUI` → segments ["Doc","GUI"] → mesh path "Doc/GUI"; `/next` (no segments) → ""
// meaning "the authenticated user's home" (resolved server-side from the token mint's nodePath,
// mirroring the SPA's no-hash default — see live.ts / server/snapshot.ts).

/** Join the catch-all segments into a mesh path. Decodes each segment, drops empties and any
 *  traversal noise; "" means "no explicit path" (→ the user's home partition). */
export function meshPathFromSegments(segments: readonly string[] | undefined | null): string {
  if (!segments || segments.length === 0) return "";
  return segments
    .map((s) => {
      try {
        return decodeURIComponent(s);
      } catch {
        return s; // malformed escape — keep the raw segment rather than 500
      }
    })
    .map((s) => s.replace(/\/+$/g, "").trim())
    .filter((s) => s.length > 0 && s !== "." && s !== "..")
    .join("/");
}

/** App-relative href for a mesh path ("" → "/", the home route). basePath is applied by Next. */
export function hrefForMeshPath(path: string): string {
  if (!path) return "/";
  return (
    "/" +
    path
      .split("/")
      .filter((s) => s.length > 0)
      .map((s) => encodeURIComponent(s))
      .join("/")
  );
}
