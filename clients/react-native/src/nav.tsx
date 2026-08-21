// Client-side navigation for the shell. Changing the target re-subscribes the live source to a new
// node/area (see App.tsx). Menu items carry an explicit `area`; content links are mesh paths.
import { createContext, useContext } from "react";

export interface NavTarget {
  address: string;
  area: string;
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
