// Light/dark theming with the SAME semantics as the Blazor portal's <FluentDesignTheme>:
// - three modes: "system" (default), "light", "dark" — Blazor's DesignThemeModes;
// - persisted in localStorage under the SAME key ("theme") and the SAME JSON shape
//   ({ mode, primaryColor? }) the fluent-design-theme web component writes, so a user switching
//   between the Blazor portal and a React app on the same origin keeps one preference;
// - "system" follows prefers-color-scheme live;
// - the resolved mode is mirrored onto <body data-theme="..."> (Blazor's UpdateBodyDataSetTheme)
//   and the document color-scheme, so scrollbars/native inputs restyle too.
//
// The Fluent design tokens (--colorNeutralBackground1, …) are applied by <FluentProvider> from
// webLightTheme/webDarkTheme — the React-v9 equivalent of Blazor's design-token stylesheet — so
// every control in the pack restyles from the one switch.

import { useCallback, useEffect, useMemo, useState } from "react";
import { webDarkTheme, webLightTheme, type Theme } from "@fluentui/react-components";

/** Blazor's DesignThemeModes: System / Light / Dark. */
export type ThemeMode = "system" | "light" | "dark";
export type ResolvedThemeMode = "light" | "dark";

/** The storage key the Blazor portal uses (`<FluentDesignTheme StorageName="theme">`). */
export const DEFAULT_THEME_STORAGE_KEY = "theme";

/** Same-document sync between hook instances (a toggle in the header + an Appearance panel). */
const THEME_CHANGE_EVENT = "meshweaver:theme-change";

function normalizeMode(value: unknown): ThemeMode | undefined {
  const s = String(value ?? "").toLowerCase();
  return s === "light" || s === "dark" || s === "system" ? s : undefined;
}

/** Read the persisted mode — tolerant of the Blazor JSON shape ({ mode, primaryColor }), a bare
 *  string ("dark"), or nothing/garbage (→ "system", the Blazor default). */
export function readStoredThemeMode(storageKey: string = DEFAULT_THEME_STORAGE_KEY): ThemeMode {
  try {
    const raw = globalThis.localStorage?.getItem(storageKey);
    if (!raw) return "system";
    try {
      const parsed = JSON.parse(raw);
      if (parsed != null && typeof parsed === "object") return normalizeMode(parsed.mode) ?? "system";
      return normalizeMode(parsed) ?? "system";
    } catch {
      return normalizeMode(raw) ?? "system";
    }
  } catch {
    return "system";
  }
}

/** Persist the mode in the Blazor FluentDesignTheme shape, preserving sibling fields
 *  (primaryColor, …) a Blazor portal may have written under the same key. */
export function writeStoredThemeMode(mode: ThemeMode, storageKey: string = DEFAULT_THEME_STORAGE_KEY): void {
  try {
    const raw = globalThis.localStorage?.getItem(storageKey);
    let existing: Record<string, unknown> = {};
    if (raw) {
      try {
        const parsed = JSON.parse(raw);
        if (parsed != null && typeof parsed === "object" && !Array.isArray(parsed)) existing = parsed;
      } catch {
        /* replace non-JSON garbage */
      }
    }
    globalThis.localStorage?.setItem(storageKey, JSON.stringify({ ...existing, mode }));
  } catch {
    /* storage unavailable (SSR / privacy mode) — theme still works, just not persisted */
  }
  globalThis.dispatchEvent?.(new CustomEvent(THEME_CHANGE_EVENT, { detail: { storageKey, mode } }));
}

/** Clear the persisted theme (Blazor's "Reset settings" → ClearLocalStorage) and go back to system. */
export function clearStoredTheme(storageKey: string = DEFAULT_THEME_STORAGE_KEY): void {
  try {
    globalThis.localStorage?.removeItem(storageKey);
  } catch {
    /* ignore */
  }
  globalThis.dispatchEvent?.(new CustomEvent(THEME_CHANGE_EVENT, { detail: { storageKey, mode: "system" } }));
}

function prefersDarkQuery(): MediaQueryList | undefined {
  return typeof window !== "undefined" && typeof window.matchMedia === "function"
    ? window.matchMedia("(prefers-color-scheme: dark)")
    : undefined;
}

export function systemPrefersDark(): boolean {
  return prefersDarkQuery()?.matches ?? false;
}

export function resolveThemeMode(mode: ThemeMode, prefersDark: boolean): ResolvedThemeMode {
  return mode === "system" ? (prefersDark ? "dark" : "light") : mode;
}

/** The Fluent theme (design-token set) for a resolved mode. */
export function fluentThemeFor(resolved: ResolvedThemeMode): Theme {
  return resolved === "dark" ? webDarkTheme : webLightTheme;
}

export interface ThemeState {
  /** The user's choice: light / dark / system. */
  mode: ThemeMode;
  /** What's actually on screen (system resolved against the OS preference). */
  resolved: ResolvedThemeMode;
  /** The Fluent theme to hand to <FluentProvider>. */
  theme: Theme;
  setMode: (mode: ThemeMode) => void;
}

export interface UseThemeModeOptions {
  storageKey?: string;
}

/**
 * The theme hook: mode persisted in localStorage (Blazor-compatible), defaulting to the OS
 * preference, live-updating on prefers-color-scheme changes, and synced across every hook
 * instance in the document (and across tabs via the storage event).
 */
export function useThemeMode(options: UseThemeModeOptions = {}): ThemeState {
  const storageKey = options.storageKey ?? DEFAULT_THEME_STORAGE_KEY;
  const [mode, setModeState] = useState<ThemeMode>(() => readStoredThemeMode(storageKey));
  const [prefersDark, setPrefersDark] = useState<boolean>(systemPrefersDark);

  // Follow the OS preference while in system mode.
  useEffect(() => {
    const mq = prefersDarkQuery();
    if (!mq) return;
    const onChange = () => setPrefersDark(mq.matches);
    // Modern API with a fallback for environments that only ship the deprecated one.
    if (typeof mq.addEventListener === "function") {
      mq.addEventListener("change", onChange);
      return () => mq.removeEventListener("change", onChange);
    }
    mq.addListener?.(onChange);
    return () => mq.removeListener?.(onChange);
  }, []);

  // Stay in sync with other hook instances (same document) and other tabs (storage event).
  useEffect(() => {
    const onLocalChange = (e: Event) => {
      const detail = (e as CustomEvent).detail;
      if (detail?.storageKey === storageKey) setModeState(readStoredThemeMode(storageKey));
    };
    const onStorage = (e: StorageEvent) => {
      if (e.key === storageKey || e.key === null) setModeState(readStoredThemeMode(storageKey));
    };
    window.addEventListener(THEME_CHANGE_EVENT, onLocalChange);
    window.addEventListener("storage", onStorage);
    return () => {
      window.removeEventListener(THEME_CHANGE_EVENT, onLocalChange);
      window.removeEventListener("storage", onStorage);
    };
  }, [storageKey]);

  const resolved = resolveThemeMode(mode, prefersDark);

  // Mirror the resolved mode onto the document — exactly what Blazor's UpdateBodyDataSetTheme does
  // (body[data-theme]) plus color-scheme so native chrome (scrollbars, inputs) follows.
  useEffect(() => {
    if (typeof document === "undefined") return;
    document.body.dataset.theme = resolved;
    document.documentElement.style.colorScheme = resolved;
  }, [resolved]);

  const setMode = useCallback(
    (m: ThemeMode) => {
      writeStoredThemeMode(m, storageKey);
      setModeState(m);
    },
    [storageKey],
  );

  return useMemo(
    () => ({ mode, resolved, theme: fluentThemeFor(resolved), setMode }),
    [mode, resolved, setMode],
  );
}
