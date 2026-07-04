"use client";

// Host wiring for @meshweaver/react's navigation seam (area/navigation.tsx). Mesh targets are
// app-absolute ("/Doc/GUI", "/search?q=…"): anchors must render WITH the Next basePath (raw
// <a href> does not get it, unlike next/link), and left-clicks must go through the App Router so
// navigation stays client-side — a full-page load on a root-absolute href escapes the /next app
// into the Blazor portal (the "clicking a card does nothing / loses the React shell" bug).

import { useMemo, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { NavigationProvider, type MeshNavigation } from "@meshweaver/react";

/** Must match next.config.mjs `basePath`. */
const BASE_PATH = "/next";

export function MeshNavigationProvider({
  children,
}: {
  children: ReactNode;
}): ReactNode {
  const router = useRouter();
  const navigation = useMemo<MeshNavigation>(
    () => ({
      hrefFor: (target) => `${BASE_PATH}${target}`,
      // router.push applies basePath itself — pass the app-absolute target through.
      navigate: (target) => router.push(target),
    }),
    [router],
  );
  return (
    <NavigationProvider navigation={navigation}>{children}</NavigationProvider>
  );
}
