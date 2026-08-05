// CommentableControl wraps arbitrary content in Blazor's select-to-comment affordance. The React
// pack renders the WRAPPED CONTENT and does not offer the affordance — the same shape as Blazor's
// own CanComment: false path.
//
// What matters here is the failure mode this prevents: an UNREGISTERED container renders as
// "Unsupported control" and its child area vanishes, so a commentable node loses its actual content
// — not just its comment button. That is what the parity guard caught, and it is what this pins.

import { beforeAll, describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";
import { controlRegistry } from "../render/registry.js";

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

function commentableTree(canComment: boolean): AreaTree {
  return {
    data: {},
    areas: {
      main: {
        $type: "Commentable",
        nodePath: "Doc/Architecture/Plugins",
        anchorText: "the wrapped source text",
        canComment,
        areas: [{ $type: "NamedArea", area: "main/Content" }],
      },
      "main/Content": { $type: "Label", data: "the wrapped content" },
    },
  };
}

describe("Commentable", () => {
  it("is registered — an unregistered container would swallow its child area", () => {
    expect(controlRegistry).toHaveProperty("Commentable");
    expect(typeof controlRegistry.Commentable).toBe("function");
  });

  it("renders the wrapped content", () => {
    render(<MeshAreaView source={new StaticAreaSource(commentableTree(true))} rootArea="main" />);

    expect(screen.getByText("the wrapped content")).toBeTruthy();
  });

  it("renders the wrapped content when commenting is disabled too", () => {
    // CanComment: false is Blazor's "render the wrapped content untouched" path — the React pack is
    // in that state permanently, so both values must look identical here.
    render(<MeshAreaView source={new StaticAreaSource(commentableTree(false))} rootArea="main" />);

    expect(screen.getByText("the wrapped content")).toBeTruthy();
  });

  it("does not render an unsupported-control notice", () => {
    const { container } = render(
      <MeshAreaView source={new StaticAreaSource(commentableTree(true))} rootArea="main" />,
    );

    expect(container.textContent).not.toContain("Unsupported control");
  });
});
