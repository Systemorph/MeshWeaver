import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { localize, resolveLocale, SUPPORTED_LOCALES } from "@meshweaver/react";

// The portal ships English and German. Blazor resolves every user-visible string through
// AccessService.Localize; portal-next had NO localization at all, so it rendered English for every
// viewer regardless of their language.
//
// This is the RATCHET that keeps the shell localized: it scans the shell components for
// user-visible literals in the attributes that carry them (title / aria-label / placeholder) and
// fails on a hard-coded one. Adding a new button with title="Do the thing" fails here until it
// becomes title={t("…")}.

const clientDir = resolve(process.cwd(), "src/client");

/** The shell components — everything a viewer reads before any layout area renders. */
const SHELL_FILES = readdirSync(clientDir).filter((f) => f.endsWith(".tsx"));

/**
 * Literals that are NOT user-visible prose and must stay as they are:
 *   - brand / product names, which are never translated (AGENTS.md's glossary rule)
 *   - single glyphs and wire identifiers
 */
const ALLOWED_LITERALS = new Set([
  "MeshWeaver", // the product name — the logo's aria-label
  "AI", // menu label; the same token in every language
  "GitHub", // brand name
  "Memex", // product name
]);

/** Every hard-coded user-visible literal bound to a text-bearing attribute in `src`. */
function hardCodedStrings(src: string): string[] {
  const out: string[] = [];
  for (const m of src.matchAll(/\b(title|aria-label|placeholder)="([^"]+)"/g)) {
    const text = m[2];
    if (ALLOWED_LITERALS.has(text)) continue;
    if (!/[A-Za-z]{2}/.test(text)) continue; // not prose (css-ish token, single symbol)
    out.push(`${m[1]}="${text}"`);
  }
  return out;
}

describe("portal-next shell is localized", () => {
  const offenders: string[] = [];
  for (const file of SHELL_FILES) {
    for (const hit of hardCodedStrings(readFileSync(resolve(clientDir, file), "utf8")))
      offenders.push(`${file}: ${hit}`);
  }

  // Guard the guard: if the scan stops finding files, or the detector stops matching, the
  // assertion below would pass vacuously on an empty list — which is exactly how a lint-style
  // ratchet rots.
  it("actually scans the shell", () => {
    expect(SHELL_FILES.length).toBeGreaterThan(8);
    expect(SHELL_FILES).toContain("SidePanel.tsx");
  });

  it("detects a hard-coded literal when there is one", () => {
    expect(hardCodedStrings('<Button title="Do the thing" />')).toEqual(['title="Do the thing"']);
    expect(hardCodedStrings('<Button title={t("common.close")} />')).toEqual([]);
    expect(hardCodedStrings('<Button title="AI" />')).toEqual([]); // allow-listed brand token
  });

  it("binds every text-bearing attribute to the catalog, not a literal", () => {
    expect(
      offenders,
      `Hard-coded UI strings — use useLocalize():\n  ${offenders.join("\n  ")}`,
    ).toEqual([]);
  });
});

describe("the catalog reaches the client", () => {
  it("resolves a shell key in both shipped languages", () => {
    for (const locale of SUPPORTED_LOCALES) {
      const text = localize("chat.closeSidePanel", locale);
      expect(text.length).toBeGreaterThan(0);
      expect(text).not.toBe("chat.closeSidePanel"); // a missing key surfaces as the raw key
    }
  });

  it("renders German for a German viewer — the gap this whole mechanism closes", () => {
    expect(localize("common.close", "de")).not.toBe(localize("common.close", "en"));
  });

  it("resolves a regional tag to its base language (de-CH → de)", () => {
    expect(resolveLocale("de-CH")).toBe("de");
    expect(localize("common.close", "de-CH")).toBe(localize("common.close", "de"));
  });
});
