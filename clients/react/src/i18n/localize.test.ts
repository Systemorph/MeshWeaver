import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  DEFAULT_LOCALE,
  LOCALE_DISPLAY_NAMES,
  SUPPORTED_LOCALES,
  catalogKeys,
  localize,
  localizePlural,
  resolveLocale,
  tryMatchLocale,
} from "./localize.js";

// Two jobs here:
//   1. DRIFT GUARD — the bundled catalogs must stay byte-identical to the server's. The server
//      catalog is the single place a string is authored; these copies exist only so the clients can
//      resolve synchronously. If someone adds a key on the server and not here (or edits a
//      translation here), this fails.
//   2. The resolution rules are a faithful port of Locales / LocalizationCatalog.

const serverDir = resolve(process.cwd(), "../../src/MeshWeaver.Messaging.Hub/Localization");
const clientDir = resolve(process.cwd(), "src/i18n");

const readJson = (p: string) => JSON.parse(readFileSync(p, "utf8")) as Record<string, string>;

describe("catalog drift guard", () => {
  for (const locale of ["en", "de"]) {
    it(`strings.${locale}.json is identical to the server catalog`, () => {
      const server = readJson(resolve(serverDir, `strings.${locale}.json`));
      const client = readJson(resolve(clientDir, `strings.${locale}.json`));
      expect(Object.keys(client).sort()).toEqual(Object.keys(server).sort());
      for (const key of Object.keys(server)) expect(client[key], `value drift for "${key}"`).toBe(server[key]);
    });
  }

  it("ships a translation for every supported language", () => {
    // The same invariant LocalizationTest enforces server-side: no language may be missing a key.
    const enKeys = new Set(catalogKeys("en"));
    expect(enKeys.size).toBeGreaterThan(500);
    for (const locale of SUPPORTED_LOCALES) {
      const missing = [...enKeys].filter((k) => !catalogKeys(locale).includes(k));
      expect(missing, `${locale} is missing: ${missing.slice(0, 10).join(", ")}`).toEqual([]);
    }
  });

  it("matches the server's Locales.Supported / DisplayNames", () => {
    const src = readFileSync(resolve(process.cwd(), "../../src/MeshWeaver.Messaging.Contract/Localization/Locales.cs"), "utf8");
    // `Supported { get; } = ["en", "de"];` — adding a language server-side must fail here until the
    // client ships its catalog too.
    const m = /Supported\s*\{\s*get;\s*\}\s*=\s*\[([^\]]*)\]/.exec(src);
    expect(m, "could not scrape Locales.Supported").toBeTruthy();
    const serverTags = [...m![1].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
    expect([...SUPPORTED_LOCALES]).toEqual(serverTags);
    for (const tag of serverTags) expect(LOCALE_DISPLAY_NAMES[tag]).toBeTruthy();
  });
});

describe("tryMatchLocale / resolveLocale (Locales port)", () => {
  it("matches a supported tag exactly, case-insensitively", () => {
    expect(tryMatchLocale("de")).toBe("de");
    expect(tryMatchLocale("DE")).toBe("de");
  });

  it("falls back to the primary subtag so de-CH / de-AT serve German", () => {
    expect(tryMatchLocale("de-CH")).toBe("de");
    expect(tryMatchLocale("de-AT")).toBe("de");
  });

  it("accepts POSIX and weighted Accept-Language shapes", () => {
    expect(tryMatchLocale("de_CH.UTF-8")).toBe("de");
    expect(tryMatchLocale("de-CH;q=0.9")).toBe("de");
  });

  it("returns null for an unsupported language — distinguishable from English", () => {
    expect(tryMatchLocale("fr")).toBeNull();
    expect(tryMatchLocale("")).toBeNull();
    expect(tryMatchLocale(null)).toBeNull();
  });

  it("resolveLocale never returns null — everything unresolvable lands on the default", () => {
    expect(resolveLocale("fr")).toBe(DEFAULT_LOCALE);
    expect(resolveLocale(undefined)).toBe(DEFAULT_LOCALE);
    expect(resolveLocale("de-CH")).toBe("de");
  });
});

describe("localize (LocalizationCatalog.Get port)", () => {
  it("resolves a key in the requested language", () => {
    expect(localize("common.close", "en")).toBe("Close");
    expect(localize("common.close", "de")).not.toBe("Close");
    expect(localize("common.close", "de").length).toBeGreaterThan(0);
  });

  it("falls back to English when the locale has no entry", () => {
    // Every key IS translated today, so assert the fallback through an unsupported language.
    expect(localize("common.close", "fr")).toBe("Close");
  });

  it("surfaces the raw key when nothing defines it — visible but harmless", () => {
    expect(localize("no.such.key.exists", "en")).toBe("no.such.key.exists");
  });

  it("substitutes positional placeholders", () => {
    // Uses a synthetic template through the missing-key path: the key itself is returned, so pick a
    // real key that carries a {0}. Fall back to asserting substitution mechanics directly.
    const withPlaceholder = Object.entries(
      JSON.parse(readFileSync(resolve(clientDir, "strings.en.json"), "utf8")) as Record<string, string>,
    ).find(([, v]) => v.includes("{0}"));
    expect(withPlaceholder, "catalog has no {0} template to exercise").toBeTruthy();
    const [key, template] = withPlaceholder!;
    expect(localize(key, "en", "XYZ")).toBe(template.replace(/\{0\}/g, "XYZ"));
  });

  it("leaves a placeholder untouched when no argument is supplied for it", () => {
    expect(localize("{0} and {1}", "en", "a")).toBe("{0} and {1}".replace("{0}", "a"));
  });
});

describe("localizePlural", () => {
  it("picks .one for exactly one and .other otherwise", () => {
    const keys = new Set(catalogKeys("en"));
    const base = [...keys].find((k) => k.endsWith(".one"))?.replace(/\.one$/, "");
    expect(base, "catalog has no plural pair to exercise").toBeTruthy();
    expect(localizePlural(base!, 1, "en")).toBe(localize(`${base}.one`, "en", 1));
    expect(localizePlural(base!, 5, "en")).toBe(localize(`${base}.other`, "en", 5));
  });
});
