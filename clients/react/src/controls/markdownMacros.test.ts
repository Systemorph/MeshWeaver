// Pins the @@("…") layout-area macro grammar the Markdown control resolves — the React mirror of
// MeshWeaver.Markdown/LayoutAreaMarkdownParser (block-level @@ = inline area embed). The user home
// dashboard (Composer/Pinned/Threads/Catalog) and doc embeds are built from exactly these macros.

import { describe, expect, it } from "vitest";
import { splitAreaMacros } from "./display.js";

describe("splitAreaMacros", () => {
  it("splits the user-home welcome template into text + region embeds", () => {
    const md = [
      "### Welcome back, Alice",
      "",
      '@@("area/Composer")',
      "",
      '@@("area/Pinned")',
      "",
      "_footer note_",
    ].join("\n");

    const segments = splitAreaMacros(md);
    expect(segments).toEqual([
      { kind: "markdown", text: "### Welcome back, Alice\n" },
      { kind: "embed", path: "area/Composer" },
      { kind: "embed", path: "area/Pinned" },
      { kind: "markdown", text: "\n_footer note_" },
    ]);
  });

  it("accepts the direct, quoted, and parenthesised syntaxes", () => {
    for (const line of ['@@("area/X")', "@@('area/X')", '@@"area/X"', "@@area/X", "@@( area/X )"]) {
      expect(splitAreaMacros(line)).toEqual([{ kind: "embed", path: "area/X" }]);
    }
  });

  it("embeds node paths as well as area references", () => {
    expect(splitAreaMacros('@@("Doc/Architecture")')).toEqual([{ kind: "embed", path: "Doc/Architecture" }]);
  });

  it("leaves inline @@ inside text untouched (macros are block-level)", () => {
    const md = 'See @@("area/X") inline reference here';
    expect(splitAreaMacros(md)).toEqual([{ kind: "markdown", text: md }]);
  });

  it("passes plain markdown through as one segment", () => {
    expect(splitAreaMacros("# Title\n\nBody")).toEqual([{ kind: "markdown", text: "# Title\n\nBody" }]);
  });
});
