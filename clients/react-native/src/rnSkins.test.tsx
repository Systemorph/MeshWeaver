import { describe, expect, it } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource, type AreaTree } from "@meshweaver/react/core";
import { rnPack } from "./rnPack";
import { gridItemSpan, paneSpec, tabLabel } from "./rnSkins";

// The thirteen skins the RN pack was missing. Two levels of proof: the pure sizing/label decisions
// (which are where a "port" silently loses the Blazor semantics), and a headless render showing the
// skinned tree lands on real native components.

type Json = ReactTestRendererJSON;

function renderTree(tree: AreaTree, root = "main"): Json {
  let r!: TestRenderer.ReactTestRenderer;
  TestRenderer.act(() => {
    r = TestRenderer.create(
      <RegistryProvider pack={rnPack}>
        <ScopeProvider source={new StaticAreaSource(tree)} area={root}>
          <RenderArea areaKey={root} />
        </ScopeProvider>
      </RegistryProvider>,
    );
  });
  return r.toJSON() as Json;
}

function* walk(node: Json | Json[] | null): Generator<Json> {
  if (node == null) return;
  if (Array.isArray(node)) {
    for (const n of node) yield* walk(n);
    return;
  }
  yield node;
  if (node.children) for (const c of node.children) yield* walk(c as Json);
}

function textOf(node: Json): string {
  let out = "";
  for (const c of node.children ?? []) {
    if (typeof c === "string") out += c;
    else if (c && typeof c === "object") out += textOf(c as Json);
  }
  return out;
}

const allText = (j: Json) => [...walk(j)].filter((n) => n.type === "Text").map(textOf);

describe("gridItemSpan — the LayoutGridItem breakpoint rule", () => {
  it("defaults to a full row when no breakpoint is set", () => {
    expect(gridItemSpan({}, 1024)).toBe(12);
  });

  it("takes the value at the LARGEST breakpoint at or below the width (mobile-first)", () => {
    const skin = { xs: 12, md: 6, xl: 3 };
    expect(gridItemSpan(skin, 500)).toBe(12); // below sm → xs
    expect(gridItemSpan(skin, 1000)).toBe(6); // md reached, xl not
    expect(gridItemSpan(skin, 2000)).toBe(3); // xl reached
  });

  it("ignores out-of-range and non-integer spans rather than laying out a broken column", () => {
    expect(gridItemSpan({ xs: 0 }, 1024)).toBe(12);
    expect(gridItemSpan({ xs: 13 }, 1024)).toBe(12);
    expect(gridItemSpan({ xs: 2.5 }, 1024)).toBe(12);
  });
});

describe("paneSpec — SplitterPane sizing", () => {
  const pane = (size?: unknown) => ({ $type: "X", skins: [{ $type: "SplitterPaneSkin", size }] }) as never;

  it("reads a PIXEL size as a fixed basis, not a star weight", () => {
    // The bug this pins: parseFloat("280px") === 280 made a fixed pane a 280:1 star weight.
    expect(paneSpec(pane("280px"))).toEqual({ fixedPx: 280, grow: 0 });
    expect(paneSpec(pane(280))).toEqual({ fixedPx: 280, grow: 0 });
  });

  it("reads star sizes as grow weights", () => {
    expect(paneSpec(pane("*"))).toEqual({ fixedPx: null, grow: 1 });
    expect(paneSpec(pane("2*"))).toEqual({ fixedPx: null, grow: 2 });
    expect(paneSpec(pane(undefined))).toEqual({ fixedPx: null, grow: 1 });
  });

  it("treats a percentage as a filling pane", () => {
    expect(paneSpec(pane("50%"))).toEqual({ fixedPx: null, grow: 1 });
  });

  it("returns a filling pane when there is no SplitterPane skin at all", () => {
    expect(paneSpec(undefined)).toEqual({ fixedPx: null, grow: 1 });
  });
});

describe("tabLabel", () => {
  it("reads the label off the child's own Tab skin", () => {
    expect(tabLabel({ $type: "X", skins: [{ $type: "TabSkin", label: "Details" }] } as never)).toBe("Details");
  });
  it("is undefined when the child carries no tab skin", () => {
    expect(tabLabel({ $type: "X", skins: [{ $type: "CardSkin" }] } as never)).toBeUndefined();
  });
});

describe("skinned trees render to native components", () => {
  it("Tabs shows every tab header but only the ACTIVE tab's body", () => {
    const tree: AreaTree = {
      areas: {
        main: {
          $type: "Stack",
          skins: [{ $type: "TabsSkin" }],
          areas: [
            { $type: "NamedArea", area: "one", id: "one" },
            { $type: "NamedArea", area: "two", id: "two" },
          ],
        },
        one: { $type: "Label", data: "first body", skins: [{ $type: "TabSkin", label: "First" }] },
        two: { $type: "Label", data: "second body", skins: [{ $type: "TabSkin", label: "Second" }] },
      },
    };
    const text = allText(renderTree(tree));
    expect(text).toContain("First");
    expect(text).toContain("Second");
    expect(text).toContain("first body");
    expect(text).not.toContain("second body"); // inactive tab body stays unmounted
  });

  it("Property renders the field's label and description around the control", () => {
    const tree: AreaTree = {
      areas: {
        main: {
          $type: "TextField",
          data: "Ada",
          skins: [{ $type: "PropertySkin", label: "Full name", description: "as it appears on the badge" }],
        },
      },
    };
    const text = allText(renderTree(tree));
    expect(text).toContain("Full name");
    expect(text).toContain("as it appears on the badge");
  });

  it("Splitter gives a pixel pane a fixed basis and a star pane the remainder", () => {
    const tree: AreaTree = {
      areas: {
        main: {
          $type: "Stack",
          skins: [{ $type: "SplitterSkin", orientation: "Horizontal" }],
          areas: [
            { $type: "NamedArea", area: "menu" },
            { $type: "NamedArea", area: "body" },
          ],
        },
        menu: { $type: "Label", data: "menu", skins: [{ $type: "SplitterPaneSkin", size: "280px" }] },
        body: { $type: "Label", data: "body", skins: [{ $type: "SplitterPaneSkin", size: "*" }] },
      },
    };
    const views = [...walk(renderTree(tree))].filter((n) => n.type === "View");
    const bases = views.map((v) => v.props.style).filter(Boolean).flat().filter(Boolean);
    expect(bases.some((s: Record<string, unknown>) => s?.flexBasis === 280 && s?.flexGrow === 0)).toBe(true);
    expect(bases.some((s: Record<string, unknown>) => s?.flexGrow === 1 && s?.flexBasis === 0)).toBe(true);
  });

  it("MenuItem hides its sub-menu until pressed", () => {
    const tree: AreaTree = {
      areas: {
        main: {
          $type: "Stack",
          skins: [{ $type: "MenuItemSkin", title: "Node", icon: "📄" }],
          areas: [{ $type: "NamedArea", area: "child" }],
        },
        child: { $type: "Label", data: "Rename" },
      },
    };
    const j = renderTree(tree);
    expect(allText(j)).toContain("Node");
    expect(allText(j)).not.toContain("Rename");
  });

  it("the semantic wrappers (Main/Header/Footer/BodyContent) render their content", () => {
    for (const skin of ["MainSkin", "HeaderSkin", "FooterSkin", "BodyContentSkin"]) {
      const tree: AreaTree = { areas: { main: { $type: "Label", data: `in ${skin}`, skins: [{ $type: skin }] } } };
      expect(allText(renderTree(tree))).toContain(`in ${skin}`);
    }
  });

  it("LayoutGridItem sizes its child by the resolved span (mock viewport = 1024 ⇒ md)", () => {
    const tree: AreaTree = {
      areas: { main: { $type: "Label", data: "cell", skins: [{ $type: "LayoutGridItemSkin", xs: 12, md: 6 }] } },
    };
    const views = [...walk(renderTree(tree))].filter((n) => n.type === "View");
    expect(views.some((v) => v.props.style?.width === "50%")).toBe(true);
  });
});
