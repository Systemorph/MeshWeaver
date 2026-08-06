import { describe, expect, it } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource, type AreaTree } from "@meshweaver/react/core";
import { rnPack } from "./rnPack";

// The container + media leaves. The regression these pin: an UNREGISTERED container renders the
// "Unsupported" fallback and its child area DISAPPEARS — a commentable node would lose its whole
// body, not just its comment affordance.

type Json = ReactTestRendererJSON;

function renderTree(tree: AreaTree): Json {
  let r!: TestRenderer.ReactTestRenderer;
  TestRenderer.act(() => {
    r = TestRenderer.create(
      <RegistryProvider pack={rnPack}>
        <ScopeProvider source={new StaticAreaSource(tree)} area="main">
          <RenderArea areaKey="main" />
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
const byType = (j: Json, t: string) => [...walk(j)].filter((n) => n.type === t);

describe("NamedArea", () => {
  it("renders the area it names", () => {
    const tree: AreaTree = {
      areas: { main: { $type: "NamedArea", area: "body" }, body: { $type: "Label", data: "the body" } },
    };
    expect(allText(renderTree(tree))).toContain("the body");
  });

  it("renders nothing (not Unsupported) when it names no area", () => {
    const text = allText(renderTree({ areas: { main: { $type: "NamedArea" } } }));
    expect(text.some((t) => t.includes("Unsupported"))).toBe(false);
  });
});

describe("Commentable", () => {
  it("renders the WRAPPED CONTENT rather than swallowing it", () => {
    const tree: AreaTree = {
      areas: {
        main: { $type: "Commentable", areas: [{ $type: "NamedArea", area: "inner" }] },
        inner: { $type: "Label", data: "commentable body" },
      },
    };
    const text = allText(renderTree(tree));
    expect(text).toContain("commentable body");
    expect(text.some((t) => t.includes("Unsupported"))).toBe(false);
  });
});

describe("Redirect", () => {
  it("renders the href as a pressable link", () => {
    const j = renderTree({ areas: { main: { $type: "Redirect", href: "/Doc/Architecture/X" } } });
    expect(allText(j)).toContain("/Doc/Architecture/X");
    expect(byType(j, "Pressable").length).toBeGreaterThan(0);
  });
});

describe("Dialog", () => {
  const tree: AreaTree = {
    areas: {
      main: {
        $type: "Dialog",
        title: "Confirm delete",
        size: "S",
        isClosable: true,
        contentArea: { area: "content" },
      },
      content: { $type: "Label", data: "This cannot be undone." },
    },
  };

  it("renders as a visible modal carrying the title and the content area", () => {
    const j = renderTree(tree);
    const modals = byType(j, "Modal");
    expect(modals).toHaveLength(1);
    expect(modals[0].props.visible).toBe(true);
    const text = allText(j);
    expect(text).toContain("Confirm delete");
    expect(text).toContain("This cannot be undone.");
  });

  it("offers a Close action when IsClosable and there are no declared actions", () => {
    expect(allText(renderTree(tree))).toContain("Close");
  });

  it("renders the ACTIONS area instead of the Close button when HasActions", () => {
    const withActions: AreaTree = {
      areas: {
        main: {
          $type: "Dialog",
          title: "Pick",
          isClosable: true,
          hasActions: true,
          contentArea: { area: "content" },
          actionsArea: { area: "actions" },
        },
        content: { $type: "Label", data: "body" },
        actions: { $type: "Button", data: "Apply" },
      },
    };
    const text = allText(renderTree(withActions));
    expect(text).toContain("Apply");
    expect(text).not.toContain("Close");
  });
});

describe("Video", () => {
  it("plays a direct source inline with native controls", () => {
    const j = renderTree({ areas: { main: { $type: "Video", src: "https://cdn/x.mp4", title: "Clip" } } });
    const vids = byType(j, "Video");
    expect(vids).toHaveLength(1);
    expect(vids[0].props.source).toEqual({ uri: "https://cdn/x.mp4" });
    expect(vids[0].props.useNativeControls).toBe(true);
  });

  it("renders an openable card for an embed (there is no native iframe)", () => {
    const j = renderTree({
      areas: { main: { $type: "Video", kind: "embed", src: "https://youtu.be/abc", title: "Talk" } },
    });
    expect(byType(j, "Video")).toHaveLength(0);
    expect(allText(j)).toContain("Talk");
    expect(byType(j, "Pressable").length).toBeGreaterThan(0);
  });

  it("renders nothing for an empty Src, exactly as Blazor does", () => {
    const j = renderTree({ areas: { main: { $type: "Video" } } });
    expect(byType(j, "Video")).toHaveLength(0);
    expect(allText(j).some((t) => t.includes("Unsupported"))).toBe(false);
  });
});

describe("SlideShow", () => {
  it("is an invisible driver — it renders no chrome, like Blazor's SlideShowView", () => {
    const j = renderTree({
      areas: { main: { $type: "SlideShow", nextHref: "/Deck/2", previousHref: "/Deck/1" } },
    });
    expect(j).toBeNull();
  });
});
