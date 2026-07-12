// Pins the LayoutGridItemSkin column spans: a grid child carrying Xs/Sm/Md spans gets the
// mobile-first .mw-gi-{bp}-{n} classes (media queries in meshStyles.ts), and one with no spans
// falls back to the full row. The skin used to be a passthrough — every span was dropped and each
// child rendered into a single 1-of-12 column.

import { beforeAll, describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl } from "../core.js";
import { GRID_BREAKPOINTS, meshStylesText } from "./meshStyles.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

function view(areas: Record<string, unknown>) {
  const source = new StaticAreaSource({ data: {}, areas: areas as Record<string, UiControl> } satisfies AreaTree);
  return render(<MeshAreaView source={source} rootArea="main" />);
}

describe("LayoutGridItem spans", () => {
  it("maps Xs/Sm/Md spans to breakpoint classes", () => {
    const { container } = view({
      main: {
        $type: "Stack",
        skins: [
          { $type: "LayoutGridItem", xs: 12, sm: 6, md: 4 },
        ],
        areas: [],
      },
    });
    const item = container.querySelector(".mw-grid-item");
    expect(item).not.toBeNull();
    expect(item!.className).toContain("mw-gi-xs-12");
    expect(item!.className).toContain("mw-gi-sm-6");
    expect(item!.className).toContain("mw-gi-md-4");
  });

  it("renders a plain full-row item when no spans are set", () => {
    const { container } = view({
      main: { $type: "Stack", skins: [{ $type: "LayoutGridItem" }], areas: [] },
    });
    const item = container.querySelector(".mw-grid-item");
    expect(item).not.toBeNull();
    expect(item!.className.trim()).toBe("mw-grid-item");
  });

  it("ships the breakpoint classes + media queries in the injected stylesheet", () => {
    const css = meshStylesText();
    expect(css).toContain(".mw-grid-item{grid-column:span 12");
    for (const [bp, min] of Object.entries(GRID_BREAKPOINTS))
      if (min > 0) expect(css).toContain(`@media (min-width:${min}px){.mw-gi-${bp}-1{grid-column:span 1}`);
  });
});
