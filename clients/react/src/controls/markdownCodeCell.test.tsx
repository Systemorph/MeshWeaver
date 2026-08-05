// The React twin of the Blazor notebook cell must read the same way: code first, the run's output
// beneath it, and the Run toolbar as a composer bar on the cell's BOTTOM edge — never above the code.
// The server-rendered path inherits the order from ExecutableCodeBlockRenderer's emission, but this
// CLIENT-side fallback (static demos / hosts without renderMarkdown) builds its own layout, so it can
// drift away from the server's shape without anything failing. This test is that guard.

import { beforeAll, describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
  if (!(globalThis as any).ResizeObserver)
    (globalThis as any).ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
});

function cellTree(markdown: string): AreaTree {
  return { data: {}, areas: { main: { $type: "Markdown", markdown } } };
}

const CELL_MARKDOWN = ["```csharp --render Demo --show-code", '"hi"', "```"].join("\n");

describe("MarkdownCodeCell layout", () => {
  it("puts the toolbar AFTER the code inside the cell frame", () => {
    const { container } = render(<MeshAreaView source={new StaticAreaSource(cellTree(CELL_MARKDOWN))} rootArea="main" />);

    const cell = container.querySelector(".md-code-cell");
    expect(cell, "the --show-code fence must render as a cell frame").not.toBeNull();

    const code = cell!.querySelector("pre");
    const toolbar = cell!.querySelector(".md-code-cell-toolbar");
    expect(code).not.toBeNull();
    expect(toolbar).not.toBeNull();

    // DOCUMENT_POSITION_FOLLOWING: the toolbar comes after the code in document order, i.e. below it.
    expect(code!.compareDocumentPosition(toolbar!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("separates the toolbar from the content above with a top border, not a bottom one", () => {
    // A bar at the foot is divided from what precedes it; keeping the old border-bottom would draw
    // the rule on the wrong edge and leave the cell looking unfinished.
    const { container } = render(<MeshAreaView source={new StaticAreaSource(cellTree(CELL_MARKDOWN))} rootArea="main" />);

    const toolbar = container.querySelector(".md-code-cell-toolbar") as HTMLElement;
    expect(toolbar.style.borderTop).toContain("1px solid");
    expect(toolbar.style.borderBottom).toBe("");
  });

  it("keeps a code-less executable block as a bare output div (no cell frame, no toolbar)", () => {
    const { container } = render(
      <MeshAreaView
        source={new StaticAreaSource(cellTree(['```csharp --render Hidden', '"hi"', "```"].join("\n")))}
        rootArea="main"
      />,
    );

    expect(container.querySelector(".md-code-cell")).toBeNull();
    expect(container.querySelector(".md-code-cell-toolbar")).toBeNull();
    expect(container.querySelector(".md-code-cell-output")).not.toBeNull();
  });
});
