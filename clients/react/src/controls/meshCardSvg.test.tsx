// Pins Issue 2: a MeshNodeCard whose imageUrl is an inline <svg> document renders it as an SVG
// ELEMENT (not escaped source text, not the bare-initial placeholder). The server-side
// GetImageUrlForNode now passes an inline-svg icon (top-level Icon / content avatar/logo/icon)
// through verbatim, so the card receives a leading "<svg" string to detect.

import { beforeAll, describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl } from "../core.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

function view(control: Record<string, unknown>) {
  const source = new StaticAreaSource({ data: {}, areas: { main: control as unknown as UiControl } } satisfies AreaTree);
  return render(<MeshAreaView source={source} rootArea="main" />);
}

const SVG = '<svg viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" fill="green"/></svg>';

describe("MeshNodeCard — inline SVG icon renders as an element", () => {
  it("mounts the <svg> from imageUrl, not the bare initial", () => {
    const { container } = view({
      $type: "MeshNodeCard",
      nodePath: "ns/X",
      title: "Example",
      imageUrl: SVG,
      disableNavigation: true,
    });
    expect(container.querySelector("svg")).not.toBeNull();
    expect(container.querySelector("circle")).not.toBeNull();
    // The raw source string must NOT appear as visible text, and the initial placeholder must not show.
    expect(container.textContent).not.toContain("<svg");
    expect(container.querySelector(".mesh-node-card-placeholder")).toBeNull();
  });
});
