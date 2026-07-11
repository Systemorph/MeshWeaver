import { describe, expect, it } from "vitest";
import React from "react";
import TestRenderer from "react-test-renderer";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource, type AreaTree } from "@meshweaver/react/core";
import { rnPack } from "./rnPack";

// Render a Draggable + DropTarget board through the RN leaf pack, headless (react-native mocked,
// Platform.OS !== "web" so the web-only DOM drag wiring is skipped). Proves the controls are
// registered and render their wrapped content via the contentArea NamedArea — without the
// Unsupported fallback. The web drag behaviour itself is covered by the Playwright e2e.

const boardTree: AreaTree = {
  data: {},
  areas: {
    main: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", orientation: "Horizontal" }],
      areas: [
        { $type: "NamedArea", area: "card" },
        { $type: "NamedArea", area: "zone" },
      ],
    },
    card: { $type: "Draggable", payload: "card-1", contentArea: { $type: "NamedArea", area: "card/Content" } },
    "card/Content": { $type: "Label", data: "Drag me" },
    zone: { $type: "DropTarget", contentArea: { $type: "NamedArea", area: "zone/Content" } },
    "zone/Content": { $type: "Label", data: "Drop here" },
  },
};

function renderTree(): string {
  let root!: TestRenderer.ReactTestRenderer;
  TestRenderer.act(() => {
    root = TestRenderer.create(
      <RegistryProvider pack={rnPack}>
        <ScopeProvider source={new StaticAreaSource(boardTree)} area="main">
          <RenderArea areaKey="main" />
        </ScopeProvider>
      </RegistryProvider>,
    );
  });
  return JSON.stringify(root.toJSON());
}

describe("RN Draggable / DropTarget", () => {
  it("renders the wrapped content of both without the Unsupported fallback", () => {
    const tree = renderTree();
    expect(tree).not.toContain("Unsupported");
    expect(tree).toContain("Drag me");
    expect(tree).toContain("Drop here");
  });
});
