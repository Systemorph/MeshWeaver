// Area-subscription error classification — the TypeScript twin of the server-side
// MeshWeaver.Layout.AreaErrorClassifier (the decisions Blazor's NamedAreaView makes on an area
// stream fault). A layout-area subscription can fault for user reasons (access denied, node gone)
// rather than engineering faults; a shell that just prints the raw string leaks framework-internal
// diagnostics ("No node found at 'u/_Activity/…'. Closest ancestor is …") and — for the course
// funnel — dead-ends a not-yet-enrolled visitor on an "Access denied" card instead of sending them
// to the public cover. These pure predicates let every shell (portal-next, react-native) make the
// SAME denial / redirect decisions the Blazor client makes, unit-tested without a renderer.
//
// Kept in sync with src/MeshWeaver.Layout/AreaErrorClassifier.cs — each predicate below has a
// matching C# method + test (test/MeshWeaver.Layout.Test/AreaErrorClassifierTest.cs).

/** What kind of user-facing failure an area error is. */
export type AreaErrorKind = "access-denied" | "not-found" | "other";

export interface AreaErrorInfo {
  /** The raw error message (for logging / verbatim display of genuine errors). */
  message: string;
  kind: AreaErrorKind;
  /** For an access-denied error: the mesh path the denial names (see getAccessDeniedPath), else null. */
  deniedPath: string | null;
}

/**
 * The mesh path an "Access denied" error names — the segment right after `permission on '`. Mirrors
 * AreaErrorClassifier.TryGetAccessDeniedPath. Both denial banners put the path there:
 *   - "Access denied: user 'u' lacks Read permission on 'AgenticEngineering'"
 *   - "User 'u' lacks Read permission on 'AgenticEngineering/Module1'"
 * Returns null when the message carries no such quoted path (a routing NotFound uses a different
 * banner and is deliberately NOT matched). The marker match is case-insensitive; the extracted path
 * keeps its original casing.
 */
export function getAccessDeniedPath(message: string | null | undefined): string | null {
  if (!message) return null;
  const marker = "permission on '";
  const open = message.toLowerCase().lastIndexOf(marker); // marker is lowercase ASCII → indices align
  if (open < 0) return null;
  const start = open + marker.length;
  const end = message.indexOf("'", start);
  return end > start ? message.slice(start, end) : null;
}

/** True when the error is an access denial (as opposed to a not-found / validation / engineering
 *  fault). A superset trigger for the "you don't have access" affordance; a non-null
 *  getAccessDeniedPath implies this. */
export function isAccessDenied(message: string | null | undefined): boolean {
  if (!message) return false;
  const m = message.toLowerCase();
  return (
    m.includes("access denied") ||
    m.includes("unauthorized") ||
    m.includes("forbidden") ||
    m.includes("permission on '") ||
    m.includes("lacks read permission") ||
    m.includes("not allowed")
  );
}

/** True when the failure is a routing NotFound ("No node found at …") — the "the thing you were
 *  viewing is gone" case. Mirrors AreaErrorClassifier.IsNodeGoneNotFound; the raw diagnostic is
 *  framework-internal and must be replaced with a graceful placeholder, never shown verbatim. */
export function isNodeGone(message: string | null | undefined): boolean {
  return !!message && message.trimStart().toLowerCase().startsWith("no node found");
}

/**
 * Whether redirecting a viewer denied `deniedPath` to `redirectPath` ("if no access ⇒ redirect
 * here") is SAFE — i.e. cannot loop. True only when a target is set AND the denied node is neither
 * the target itself nor a node under it (redirecting the target or its subtree back to the target
 * would bounce forever). A leading '/' on the target is ignored. Mirrors
 * AreaErrorClassifier.IsSafeRedirect. The redirect TARGET itself comes from the node's
 * PartitionAccessPolicy (the resolve response's `redirectOnDenied`), not from this function.
 */
export function isSafeRedirect(
  deniedPath: string | null | undefined,
  redirectPath: string | null | undefined,
): boolean {
  if (!deniedPath || !redirectPath || redirectPath.trim().length === 0) return false;
  const target = redirectPath.trim().replace(/^\/+/, "");
  if (target.length === 0) return false;
  return deniedPath !== target && !deniedPath.startsWith(target + "/");
}

/** Classify an area error string into a user-facing kind + the denied path (if any). Returns null
 *  for a null/empty message. The single entry point shells use to decide redirect vs. denied vs.
 *  gone vs. generic-error rendering. */
export function classifyAreaError(message: string | null | undefined): AreaErrorInfo | null {
  if (!message) return null;
  const deniedPath = getAccessDeniedPath(message);
  const kind: AreaErrorKind =
    deniedPath !== null || isAccessDenied(message)
      ? "access-denied"
      : isNodeGone(message)
        ? "not-found"
        : "other";
  return { message, kind, deniedPath };
}
