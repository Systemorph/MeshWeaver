// Client-side navigation for the shell. Changing the target re-subscribes the live source to a new
// node/area (see App.tsx). Menu items carry an explicit `area`; content links are mesh paths.
import { createContext, useContext } from "react";

export interface NavTarget {
  address: string;
  area: string;
  /** Optional area instance id — carries the query string too (LayoutAreaReference.Id parses
   *  `?name=value` parameters off it, e.g. the Store category filter). */
  id?: string;
}

export const NavContext = createContext<(t: NavTarget) => void>(() => {});
export const useNavigate = (): ((t: NavTarget) => void) => useContext(NavContext);

/** The address currently shown — so relative content links resolve against it. */
export const CurrentAddressContext = createContext<string>("");
export const useCurrentAddress = (): string => useContext(CurrentAddressContext);

/**
 * Resolve a mesh href to a nav target. Absolute (`/Doc/Architecture/X`) and relative (`Sibling`,
 * `../Other`) links both resolve to the node with an EMPTY area — the server renders the node's
 * DECLARED default area (the same standard-layout resolution Blazor and portal-next use; the tree
 * carries a `""` indirection to the resolved area). In-page anchors (`#x`) and external `http(s)`
 * links return null (handled elsewhere / ignored).
 */
export function parseHref(href: string, currentAddress: string): NavTarget | null {
  if (!href || href.startsWith("#") || /^https?:\/\//i.test(href) || href.startsWith("mailto:")) return null;
  const raw = href.startsWith("/") ? href.slice(1) : `${currentAddress}/${href}`;
  const parts: string[] = [];
  for (const seg of raw.split("/")) {
    if (seg === "..") parts.pop();
    else if (seg && seg !== ".") parts.push(seg);
  }
  // The ROOT link ("/" — e.g. the settings nav's Home entry) is a real target: the shell's home.
  // An empty address is the "go home" sentinel App.navigate resolves; only a RELATIVE parse that
  // dissolved to nothing is a miss.
  if (parts.length === 0) return href.startsWith("/") ? { address: "", area: "" } : null;
  return { address: parts.join("/"), area: "" };
}

/**
 * Resolve a server-sent NavigationRequest uri ("{path}[/{area}[/{id}]][?query]") into a NavTarget —
 * the client twin of Blazor's NavigationService.NavigateTo(uri) + route resolution. The node/area
 * split is the SERVER's (`POST /api/mesh/resolve`, the same verb portal-next resolves URLs with):
 * a node path can span many segments, so the client never guesses the prefix. The query string
 * rides in the id (LayoutAreaReference.Id parses `?name=value` off it — the Store's
 * `Catalog?category=Games` shape). Falls back to whole-path + default area when the verb is
 * missing or fails, matching portal-next's fetchAreaTarget.
 */
export async function resolveNavigationUri(
  uri: string,
  baseUrl: string,
  token?: string,
): Promise<NavTarget> {
  const qIndex = uri.indexOf("?");
  const path = (qIndex < 0 ? uri : uri.slice(0, qIndex)).replace(/^\/+|\/+$/g, "");
  const query = qIndex < 0 ? "" : uri.slice(qIndex);
  const fallback: NavTarget = { address: path, area: "", id: query || undefined };
  if (!path) return fallback;
  try {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (token) headers.Authorization = `Bearer ${token}`;
    const resp = await fetch(`${baseUrl.replace(/\/+$/, "")}/api/mesh/resolve`, {
      method: "POST",
      headers,
      body: JSON.stringify({ path }),
    });
    if (!resp.ok) return fallback;
    const text = await resp.text();
    if (text.startsWith("Error:") || text.startsWith("Not found:")) return fallback;
    const parsed = JSON.parse(text) as { prefix?: string; remainder?: string | null };
    const prefix = String(parsed.prefix ?? "");
    if (!prefix) return fallback;
    const remainder = String(parsed.remainder ?? "").replace(/^\/+|\/+$/g, "");
    const slash = remainder.indexOf("/");
    const area = slash < 0 ? remainder : remainder.slice(0, slash);
    const idPath = slash < 0 ? "" : remainder.slice(slash + 1);
    // The query rides in the Id, PREFIXED by a path — the Store contract is Id
    // "Catalog?category=…" (CategoryHref): with no id segment of its own, the area name is the
    // Id's path half. A bare "?category=…" reads as a data-collection id server-side and fails
    // with "Collection ?category=… is not mapped".
    const id = query ? `${idPath || area}${query}` : idPath;
    return { address: prefix, area, id: id || undefined };
  } catch {
    return fallback;
  }
}
