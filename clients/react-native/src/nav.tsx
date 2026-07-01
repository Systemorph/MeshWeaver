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
 * `../Other`) links both resolve to a node whose default area is `Overview`; in-page anchors (`#x`)
 * and external `http(s)` links return null (handled elsewhere / ignored).
 */
export function parseHref(href: string, currentAddress: string): NavTarget | null {
  if (!href || href.startsWith("#") || /^https?:\/\//i.test(href) || href.startsWith("mailto:")) return null;
  const raw = href.startsWith("/") ? href.slice(1) : `${currentAddress}/${href}`;
  const parts: string[] = [];
  for (const seg of raw.split("/")) {
    if (seg === "..") parts.pop();
    else if (seg && seg !== ".") parts.push(seg);
  }
  if (parts.length === 0) return null;
  return { address: parts.join("/"), area: "Overview" };
}
