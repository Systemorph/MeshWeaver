// Pins the @@("…") layout-area macro grammar the Markdown control resolves — the React mirror of
// MeshWeaver.Markdown/LayoutAreaMarkdownParser (block-level @@ = inline area embed). The user home
// dashboard (Composer/Pinned/Threads/Catalog) and doc embeds are built from exactly these macros.

import { describe, expect, it } from "vitest";
import { splitAreaMacros, stripAnnotations } from "./display.js";
import { parseInteractiveMarkdown, splitRenderedHtml } from "./interactiveMarkdown.js";

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

describe("splitRenderedHtml (Markdig-pipeline HTML hydration)", () => {
  it("splits html chunks at layout-area, toolbar, and mermaid markers", () => {
    const html =
      "<h1>Title</h1>\n" +
      "<div class='layout-area' data-address='__KERNEL_ADDRESS__' data-area='HelloWorld' data-area-id=''></div>" +
      '<div class="md-code-cell-toolbar" data-submission-id="HelloWorld" data-language="csharp"></div>' +
      "<div class='layout-area' data-raw-path='area/Composer'></div>" +
      "<div class='layout-area' data-address='ACME/Pricing' data-area='Overview' data-area-id='tab1'></div>" +
      "<div class='mermaid'>\ngraph TD; A--&gt;B;\n</div>" +
      "<p>tail</p>";

    const segments = splitRenderedHtml(html);
    expect(segments.map((s) => s.kind)).toEqual([
      "html", "area", "toolbar", "area", "area", "mermaidHtml", "html",
    ]);
    const kernelArea = segments[1] as { isKernel: boolean; area?: string };
    expect(kernelArea.isKernel).toBe(true);
    expect(kernelArea.area).toBe("HelloWorld");
    const toolbar = segments[2] as { submissionId: string; language: string };
    expect(toolbar).toMatchObject({ submissionId: "HelloWorld", language: "csharp" });
    const macroArea = segments[3] as { rawPath?: string; isKernel: boolean };
    expect(macroArea).toMatchObject({ rawPath: "area/Composer", isKernel: false });
    const resolved = segments[4] as { address?: string; area?: string; id?: string };
    expect(resolved).toMatchObject({ address: "ACME/Pricing", area: "Overview", id: "tab1" });
    const mermaid = segments[5] as { code: string };
    expect(mermaid.code).toBe("graph TD; A-->B;");
  });

  it("passes marker-free html through as one chunk", () => {
    expect(splitRenderedHtml("<p>plain</p>")).toEqual([{ kind: "html", html: "<p>plain</p>" }]);
  });
});

describe("parseInteractiveMarkdown (client-side fallback)", () => {
  it("extracts --render cells with show flags and keeps plain fences as markdown", () => {
    const md = [
      "intro",
      "```csharp --render Hello --show-code",
      '"Hello" + 1',
      "```",
      "```csharp",
      "plain sample",
      "```",
    ].join("\n");
    const segments = parseInteractiveMarkdown(md);
    expect(segments.map((s) => s.kind)).toEqual(["markdown", "cell", "markdown"]);
    expect(segments[1]).toMatchObject({
      kind: "cell", submissionId: "Hello", hasOutput: true, showCode: true, showHeader: false,
    });
  });

  it("keeps @@ macros literal inside fences", () => {
    const md = ["```csharp", '@@("area/X")', "```"].join("\n");
    const segments = parseInteractiveMarkdown(md);
    expect(segments).toHaveLength(1);
    expect(segments[0].kind).toBe("markdown");
  });
});

describe("stripAnnotations (CollaborativeMarkdown read-only display)", () => {
  it("degrades CriticMarkup markers to plain display text", () => {
    expect(stripAnnotations("keep {++added++} and {--gone--}{~~old~>new~~} {==mark==}{>>note<<}."))
      .toBe("keep added and new mark.");
  });

  it("passes unannotated markdown through untouched", () => {
    expect(stripAnnotations("# Title\n\nBody")).toBe("# Title\n\nBody");
  });
});
