"use client";

// Hydration-safe theming: the server (and the client's FIRST render, which must match it) pins the
// light theme; after mount the user's persisted light/dark/system preference takes over via the
// renderer's useThemeMode (localStorage["theme"] + prefers-color-scheme — browser-only APIs, hence
// the mounted gate). Same key/shape as the Blazor portal and the Vite SPA.

import { useEffect, useState } from "react";
import type { Theme } from "@fluentui/react-components";
import { fluentThemeFor, useThemeMode, type ThemeState } from "@meshweaver/react";

export interface HydratedTheme extends Omit<ThemeState, "theme"> {
  theme: Theme;
  /** False during SSR + the hydration render; true once client-only state may differ from SSR. */
  mounted: boolean;
}

export function useHydratedTheme(): HydratedTheme {
  const themeState = useThemeMode();
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(true), []);
  return {
    ...themeState,
    mounted,
    theme: mounted ? themeState.theme : fluentThemeFor("light"),
  };
}
