// Light / dark theming for the shell chrome. The mesh CONTENT (injected .markdown-body HTML) is themed by
// the vendored GitHub CSS (light default + dark scoped under [data-theme="dark"], see webStyles.ts); the
// CHROME (react-native-web StyleSheet) can't consume CSS variables reliably, so it reads a Palette instead.
// The chosen mode is written to <html data-theme> (so the markdown CSS switches) and persisted.
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";

export type ThemeMode = "light" | "dark";

export interface Palette {
  appBg: string;
  topbarBg: string;
  surface: string;
  border: string;
  sidebarBg: string;
  rightBg: string;
  text: string;
  textSubtle: string;
  textMuted: string;
  accent: string;
  onAccent: string;
  navHover: string;
  navActiveBg: string;
  navActiveText: string;
  inputBg: string;
}

const LIGHT: Palette = {
  appBg: "#ffffff", topbarBg: "#faf9f8", surface: "#ffffff", border: "#e3e3e3",
  sidebarBg: "#f5f5f7", rightBg: "#faf9f8", text: "#242424", textSubtle: "#616161",
  textMuted: "#8a8a8a", accent: "#0f6cbd", onAccent: "#ffffff", navHover: "#ececee",
  navActiveBg: "#e1ebf7", navActiveText: "#0f6cbd", inputBg: "#ffffff",
};

const DARK: Palette = {
  appBg: "#1b1a19", topbarBg: "#252423", surface: "#2d2c2b", border: "#3b3a39",
  sidebarBg: "#201f1e", rightBg: "#252423", text: "#f3f2f1", textSubtle: "#c8c6c4",
  textMuted: "#979593", accent: "#4aa3e8", onAccent: "#ffffff", navHover: "#323130",
  navActiveBg: "#2b3a4a", navActiveText: "#6cb8f0", inputBg: "#1b1a19",
};

interface ThemeCtx {
  mode: ThemeMode;
  palette: Palette;
  toggle: () => void;
}

const Ctx = createContext<ThemeCtx>({ mode: "light", palette: LIGHT, toggle: () => {} });

const STORAGE_KEY = "mw.theme";

function initialMode(): ThemeMode {
  if (typeof window === "undefined") return "light";
  try {
    const saved = window.localStorage?.getItem(STORAGE_KEY);
    if (saved === "light" || saved === "dark") return saved;
  } catch { /* storage disabled */ }
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export function ThemeProvider({ children }: { children: ReactNode }): ReactNode {
  const [mode, setMode] = useState<ThemeMode>(initialMode);

  useEffect(() => {
    if (typeof document !== "undefined") document.documentElement.setAttribute("data-theme", mode);
    try { window.localStorage?.setItem(STORAGE_KEY, mode); } catch { /* ignore */ }
  }, [mode]);

  const value = useMemo<ThemeCtx>(
    () => ({ mode, palette: mode === "dark" ? DARK : LIGHT, toggle: () => setMode((m) => (m === "dark" ? "light" : "dark")) }),
    [mode],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export const useTheme = (): ThemeCtx => useContext(Ctx);

/** Memoize a StyleSheet factory against the current palette. */
export function useStyles<T>(factory: (p: Palette) => T): T {
  const { palette } = useTheme();
  return useMemo(() => factory(palette), [palette, factory]);
}
