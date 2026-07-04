// Navigation seam — the React analog of Blazor's NavigationManager: ONE place that maps an
// app-absolute mesh target ("/Doc/GUI", "/rbuergi/Thread/x", "/search?q=…") onto the HOST's
// routing scheme. Every internal link the renderer emits (mesh result cards, nav links,
// navigateToHref buttons, redirect targets, anchors inside server-rendered markdown HTML)
// resolves BOTH its href and its click through this context:
//   - portal-next supplies { hrefFor: t => `/next${t}`, navigate: router.push } (Next basePath),
//   - the Vite SPA supplies { hrefFor: t => `#${t}`, navigate: hash-route } (its "#/{path}" router),
//   - without a provider links keep their root-absolute shape and clicks full-page navigate —
//     the standalone/demo behavior, and exactly what the Blazor-served origin expects.
// Hard-coding `/${path}` hrefs in controls was the "click does not work" bug: under a base path
// (portal-next) or a hash router (the SPA) a root-absolute anchor escapes the app entirely.

import { createContext, useContext, useMemo, type MouseEvent, type ReactNode } from "react";

export interface MeshNavigation {
  /** Map an app-absolute target ("/Doc/GUI", "/search?q=x") to the href the anchor renders. */
  hrefFor(target: string): string;
  /** Client-side navigate to an app-absolute target. */
  navigate(target: string): void;
}

const defaultNavigation: MeshNavigation = {
  hrefFor: (target) => target,
  navigate: (target) => {
    if (typeof window !== "undefined") window.location.assign(target);
  },
};

const NavigationContext = createContext<MeshNavigation>(defaultNavigation);

export function NavigationProvider({ navigation, children }: { navigation: MeshNavigation; children: ReactNode }): ReactNode {
  return <NavigationContext.Provider value={navigation}>{children}</NavigationContext.Provider>;
}

export function useNavigation(): MeshNavigation {
  return useContext(NavigationContext);
}

/** External targets (scheme-full, protocol-relative, fragment, mailto…) bypass the host router. */
export function isExternalTarget(target: string): boolean {
  return !target.startsWith("/") || target.startsWith("//");
}

/** True for the clicks a router may claim — plain left-clicks without modifiers on self targets. */
function isRoutableClick(e: MouseEvent): boolean {
  return !e.defaultPrevented && e.button === 0 && !e.metaKey && !e.ctrlKey && !e.shiftKey && !e.altKey;
}

export interface MeshLink {
  href?: string;
  onClick?: (e: MouseEvent) => void;
}

/**
 * href + onClick pair for a link to an app-absolute target. Internal targets render the host's
 * href (correct for copy-link/middle-click) and left-clicks route client-side; external targets
 * pass through untouched. An absent/empty target yields an inert pair.
 */
export function useMeshLink(target: string | undefined): MeshLink {
  const navigation = useNavigation();
  return useMemo(() => {
    if (!target) return {};
    if (isExternalTarget(target)) return { href: target };
    return {
      href: navigation.hrefFor(target),
      onClick: (e: MouseEvent) => {
        if (!isRoutableClick(e)) return;
        e.preventDefault();
        navigation.navigate(target);
      },
    };
  }, [navigation, target]);
}

/**
 * Click handler for containers holding INJECTED HTML (server-rendered markdown, HtmlControl):
 * those anchors are not React elements, so route their internal root-absolute hrefs through the
 * host here instead. Anchors with a target/download, fragments, and external URLs fall through
 * to the browser.
 */
export function useHtmlLinkInterceptor(): (e: MouseEvent<HTMLElement>) => void {
  const navigation = useNavigation();
  return (e) => {
    if (!isRoutableClick(e)) return;
    const anchor = (e.target as HTMLElement).closest?.("a[href]");
    if (!anchor || !e.currentTarget.contains(anchor)) return;
    const href = anchor.getAttribute("href") ?? "";
    if (isExternalTarget(href) || anchor.getAttribute("target") || anchor.hasAttribute("download")) return;
    e.preventDefault();
    navigation.navigate(href);
  };
}
