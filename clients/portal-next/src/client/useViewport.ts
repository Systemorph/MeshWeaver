"use client";

// Viewport classification — the client twin of the Blazor portal's ViewportInformation
// (src/MeshWeaver.Blazor.Portal/Resize/ViewportInformation.cs): desktop is width > 768px.
// SSR and the first client render report desktop (matching the server markup); the real
// classification lands after mount via matchMedia.

import { useSyncExternalStore } from "react";

/** The Blazor portal's mobile breakpoint (ViewportInformation.cs). */
export const MOBILE_BREAKPOINT_PX = 768;

const QUERY = `(max-width: ${MOBILE_BREAKPOINT_PX}px)`;

function subscribe(onChange: () => void): () => void {
  const mql = window.matchMedia(QUERY);
  mql.addEventListener("change", onChange);
  return () => mql.removeEventListener("change", onChange);
}

/** True when the viewport is mobile-sized (≤768px). Always false during SSR/hydration. */
export function useIsMobile(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => window.matchMedia(QUERY).matches,
    () => false,
  );
}
