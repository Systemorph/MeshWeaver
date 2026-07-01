// @vitest-environment jsdom
// Snapshot → StaticAreaSource seeding: the tree synthesized by the server module renders through
// the real @meshweaver/react registry, seeded into a StaticAreaSource exactly as the LiveArea
// client boundary does before live takeover.
import { describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { webLightTheme } from "@fluentui/react-components";
import { MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import { buildInitialTree, SSR_ROOT_AREA, type NodeSnapshot } from "../src/server/snapshot";

const snapshot: NodeSnapshot = {
  path: "Doc/GUI",
  name: "GUI Documentation",
  nodeType: "Markdown",
  markdown: "The **mesh** streams layout areas.",
};

describe("snapshot → StaticAreaSource seeding", () => {
  it("builds the preview tree rooted at the live default-area key", () => {
    const tree = buildInitialTree(snapshot);
    expect(tree.areas?.[SSR_ROOT_AREA]).toBeTruthy();
    expect(SSR_ROOT_AREA).toBe(""); // the live subscription's root — takeover swaps the source only
    expect(tree.areas?.ssrTitle).toMatchObject({ $type: "Label", data: "GUI Documentation" });
    expect(tree.areas?.ssrBody).toMatchObject({ $type: "Markdown" });
  });

  it("omits the body area when the node carries no markdown-ish content", () => {
    const tree = buildInitialTree({ path: "A", name: "A" });
    expect(tree.areas?.ssrBody).toBeUndefined();
  });

  it("renders the seeded tree through the real registry", async () => {
    const source = new StaticAreaSource(buildInitialTree(snapshot));
    render(<MeshAreaView source={source} rootArea={SSR_ROOT_AREA} theme={webLightTheme} />);

    expect(screen.getByText("GUI Documentation")).toBeTruthy();
    expect(screen.getByText("Markdown · Doc/GUI")).toBeTruthy();
    // react-markdown renders the body: the bold run arrives as a <strong>.
    expect((await screen.findByText("mesh")).tagName).toBe("STRONG");
    cleanup();
  });
});
