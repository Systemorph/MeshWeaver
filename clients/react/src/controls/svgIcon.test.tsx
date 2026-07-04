// Pins the "SVGs are not displayed" fix: an inline <svg> Icon must render as an SVG ELEMENT, not as
// escaped source text (the old IconView fell through to <span>{"<svg …>"}</span>).

import { beforeAll, describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl } from "../core.js";
import { isInlineSvg } from "./display.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

function view(control: Record<string, unknown>) {
  const source = new StaticAreaSource({ data: {}, areas: { main: control as unknown as UiControl } } satisfies AreaTree);
  return render(<MeshAreaView source={source} rootArea="main" />);
}

const SVG = '<svg viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" fill="red"/></svg>';

describe("isInlineSvg", () => {
  it("matches an inline SVG document, not names/urls/data-uris", () => {
    expect(isInlineSvg(SVG)).toBe(true);
    expect(isInlineSvg("  <svg>x</svg>")).toBe(true);
    expect(isInlineSvg("Save")).toBe(false);
    expect(isInlineSvg("https://x/y.svg")).toBe(false);
    expect(isInlineSvg("data:image/svg+xml,<svg/>")).toBe(false);
  });
});

describe("IconView — inline SVG renders as an element, not text", () => {
  it("mounts the <svg> in the DOM", () => {
    const { container } = view({ $type: "Icon", data: SVG });
    expect(container.querySelector("svg")).not.toBeNull();
    expect(container.querySelector("circle")).not.toBeNull();
    // The raw source string must NOT appear as visible text.
    expect(container.textContent).not.toContain("<svg");
  });
});
