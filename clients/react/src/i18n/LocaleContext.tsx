// The viewer's locale, carried down the tree — the client twin of AccessContext.Locale.
//
// 🚨 The server rule this mirrors: resolution is ALWAYS explicit, never read from an ambient
// culture. Blazor never resolves from CultureInfo.CurrentUICulture because a layout-area render
// hops the hub scheduler and an AsyncLocal culture does not survive it — one user's UI would pick
// up another user's language. The client analogue is the same discipline: the locale comes from
// this provider (seeded from the signed-in user's User.Locale), not from navigator.language at the
// point of use.

import { createContext, useContext, useMemo, type ReactNode } from "react";
import { localize, localizePlural, resolveLocale, DEFAULT_LOCALE } from "./localize.js";

export interface LocaleContextValue {
  /** The resolved, SUPPORTED tag ("en" | "de") — never a raw browser string. */
  locale: string;
  /** Resolve a catalog key, substituting positional {0} placeholders. */
  t(key: string, ...args: unknown[]): string;
  /** Plural-aware resolve ({key}.one / {key}.other). */
  tPlural(key: string, count: number): string;
}

const LocaleContext = createContext<LocaleContextValue>({
  locale: DEFAULT_LOCALE,
  t: (key: string, ...args: unknown[]) => localize(key, DEFAULT_LOCALE, ...args),
  tPlural: (key: string, count: number) => localizePlural(key, count, DEFAULT_LOCALE),
});

/**
 * Provide the viewer's locale. `value` is an arbitrary requested tag (the user node's `locale`, or
 * a browser language as a last resort) — it is resolved to a supported one here, once.
 */
export function LocaleProvider({ locale, children }: { locale?: string | null; children: ReactNode }) {
  const value = useMemo<LocaleContextValue>(() => {
    const resolved = resolveLocale(locale);
    return {
      locale: resolved,
      t: (key: string, ...args: unknown[]) => localize(key, resolved, ...args),
      tPlural: (key: string, count: number) => localizePlural(key, count, resolved),
    };
  }, [locale]);
  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}

/** The localizer for the current viewer. Use in any component that renders user-visible text. */
export function useLocale(): LocaleContextValue {
  return useContext(LocaleContext);
}

/** Shorthand for the common case — `const t = useLocalize();  t("common.close")`. */
export function useLocalize(): (key: string, ...args: unknown[]) => string {
  return useContext(LocaleContext).t;
}
