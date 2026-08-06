// Client-side localization — the TS twin of the server's Locales + LocalizationCatalog.
//
// The portal ships English and German. Blazor resolves every user-visible string through
// AccessService.Localize (src/MeshWeaver.Messaging.Hub/Localization), so a German viewer sees
// German. The JS clients had NO localization at all: every string was hard-coded English, which
// renders English for every viewer regardless of their language — a functional parity gap, and a
// violation of the repo's "a hard-coded UI string is a bug" rule.
//
// The CATALOG is the server's. strings.{en,de}.json here are copies of
// src/MeshWeaver.Messaging.Hub/Localization/strings.{en,de}.json, and localize.test.ts fails if
// they drift by so much as one key — so there is still exactly ONE place a string is authored.
// Bundling rather than fetching keeps the resolution synchronous (no flash of untranslated text)
// and works for the RN sidecar, which has no localization endpoint to call.

import en from "./strings.en.json" with { type: "json" };
import de from "./strings.de.json" with { type: "json" };

export type LocaleTag = string;

/** The fallback language. Every unsupported, empty or unresolvable tag lands here. */
export const DEFAULT_LOCALE = "en";

/** The languages this deployment ships translations for, in display order (Locales.Supported). */
export const SUPPORTED_LOCALES: readonly string[] = ["en", "de"];

/** Endonyms for the settings picker — a German speaker looks for "Deutsch" (Locales.DisplayNames). */
export const LOCALE_DISPLAY_NAMES: Readonly<Record<string, string>> = { en: "English", de: "Deutsch" };

const CATALOGS: Record<string, Record<string, string>> = {
  en: en as Record<string, string>,
  de: de as Record<string, string>,
};

/**
 * Returns the supported tag matching `requested`, or null when this deployment ships no
 * translation for it. Port of Locales.TryMatch: accepts BCP-47 ("de-CH"), POSIX ("de_CH.UTF-8")
 * and weighted Accept-Language ("de-CH;q=0.9") shapes, matching exactly then by primary subtag.
 */
export function tryMatchLocale(requested: string | null | undefined): string | null {
  if (!requested || !requested.trim()) return null;
  const tag = requested.trim().split(/[.;]/)[0].replace(/_/g, "-");
  for (const supported of SUPPORTED_LOCALES) if (supported.toLowerCase() === tag.toLowerCase()) return supported;
  const primary = tag.split("-")[0];
  if (!primary) return null;
  for (const supported of SUPPORTED_LOCALES) if (supported.toLowerCase() === primary.toLowerCase()) return supported;
  return null;
}

/** Resolves an arbitrary tag to a supported language, never throwing (Locales.Resolve). */
export function resolveLocale(requested: string | null | undefined): string {
  return tryMatchLocale(requested) ?? DEFAULT_LOCALE;
}

/**
 * Look a key up, falling back locale → English → the key itself. A missing key surfaces as the raw
 * key, which is visible-but-harmless and makes the gap obvious in review — the server's rule.
 */
function lookup(key: string, locale: string | null | undefined): string {
  const resolved = resolveLocale(locale);
  const catalog = CATALOGS[resolved];
  if (catalog && key in catalog) return catalog[key];
  if (resolved !== DEFAULT_LOCALE) {
    const fallback = CATALOGS[DEFAULT_LOCALE];
    if (fallback && key in fallback) return fallback[key];
  }
  return key;
}

/**
 * Resolve `key` for `locale`, substituting positional `{0}`-style placeholders from `args`.
 * Placeholders are positional so a translator may reorder them for target-language word order
 * without touching code (LocalizationCatalog.Get).
 */
export function localize(key: string, locale: string | null | undefined, ...args: unknown[]): string {
  const text = lookup(key, locale);
  if (args.length === 0) return text;
  return text.replace(/\{(\d+)\}/g, (whole, i: string) => {
    const v = args[Number(i)];
    return v === undefined ? whole : String(v);
  });
}

/**
 * Plural-aware lookup: `{key}.one` when count is exactly 1, `{key}.other` otherwise, with the
 * count formatted in as `{0}`. English and German share this one/other split
 * (LocalizationCatalog.Plural).
 */
export function localizePlural(key: string, count: number, locale: string | null | undefined): string {
  return localize(count === 1 ? `${key}.one` : `${key}.other`, locale, count);
}

/** Every key the catalog defines for a locale — used by the drift guard. */
export function catalogKeys(locale: string): string[] {
  return Object.keys(CATALOGS[resolveLocale(locale)] ?? {});
}
