import { beforeAll, describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";

// jsdom lacks the browser APIs several Fluent components probe on mount.
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

function view(tree: AreaTree, rootArea = "main") {
  return render(<MeshAreaView source={new StaticAreaSource(tree)} rootArea={rootArea} />);
}

// Fix 1: a control's WithClass value (wire `class`) must land on its root DOM element — for BOTH
// containers (rendered via skins) and leaf controls — merged with any hardcoded class, never dropped.
describe("controlClass (WithClass) is applied, not dropped", () => {
  it("lands on a container's skin root div", () => {
    const { container } = view({
      areas: {
        main: { $type: "Stack", class: "settings-section", skins: [{ $type: "LayoutStack" }], areas: [{ $type: "NamedArea", area: "body" }] },
        body: { $type: "Label", data: "Hi" },
      },
    });
    expect(container.querySelector("div.settings-section")).not.toBeNull();
  });

  it("lands on a leaf control's root, merged with the control's own class", () => {
    const { container } = view({
      areas: {
        main: { $type: "Stack", skins: [{ $type: "LayoutStack" }], areas: [{ $type: "NamedArea", area: "l" }, { $type: "NamedArea", area: "m" }] },
        l: { $type: "Label", class: "my-label", data: "Hello" },
        m: { $type: "Markdown", class: "my-md", data: "text" },
      },
    });
    expect(container.querySelector(".my-label")).not.toBeNull();
    const md = container.querySelector(".my-md");
    expect(md).not.toBeNull();
    // merged, not replaced — the framework `mw-markdown` class survives.
    expect(md?.classList.contains("mw-markdown")).toBe(true);
  });
});

// Fix 1b: a control's inline WithStyle string must land on its root element for BOTH a container
// (skin) and a Button leaf — otherwise an absolutely-positioned overlay stack (the pinned-card unpin
// toggle) renders as a full-width bar in flow, and an icon-only circular button renders at default
// width. The style must WIN over the skin/component defaults.
describe("inline style (WithStyle) is honored on Stack and Button", () => {
  it("an overlay Stack's position:absolute escapes flow and wins over skin defaults", () => {
    const { container } = view({
      areas: {
        main: {
          $type: "Stack",
          class: "pin-overlay",
          style: "position: absolute; top: 6px; right: 6px; z-index: 5;",
          skins: [{ $type: "LayoutStack" }],
          areas: [{ $type: "NamedArea", area: "b" }],
        },
        b: { $type: "Label", data: "x" },
      },
    });
    const overlay = container.querySelector<HTMLElement>(".pin-overlay");
    expect(overlay).not.toBeNull();
    expect(overlay!.style.position).toBe("absolute");
    expect(overlay!.style.top).toBe("6px");
    expect(overlay!.style.right).toBe("6px");
    expect(overlay!.style.zIndex).toBe("5");
    // The skin's flex layout still applies underneath the overlay positioning.
    expect(overlay!.style.display).toBe("flex");
  });

  it("a Button's inline size/border-radius lands on the rendered button", () => {
    const { container } = view({
      areas: {
        main: { $type: "Stack", skins: [{ $type: "LayoutStack" }], areas: [{ $type: "NamedArea", area: "btn" }] },
        btn: {
          $type: "Button",
          data: "",
          style: "min-width: 28px; width: 28px; height: 28px; border-radius: 50%;",
        },
      },
    });
    const button = container.querySelector<HTMLElement>("button");
    expect(button).not.toBeNull();
    expect(button!.style.minWidth).toBe("28px");
    expect(button!.style.width).toBe("28px");
    expect(button!.style.height).toBe("28px");
    expect(button!.style.borderRadius).toBe("50%");
  });
});

// Fix 2: a pane sized "280px" must be a FIXED 280px column; a "*" pane fills the remainder. The old
// code fed both through parseFloat → weights 280:1, ballooning the fixed menu to ~full width.
describe("SplitterSkin sizes px vs star correctly", () => {
  const splitter: AreaTree = {
    areas: {
      main: {
        $type: "Splitter",
        skins: [{ $type: "Splitter", orientation: "Horizontal", width: "100%" }],
        areas: [{ $type: "NamedArea", area: "menu" }, { $type: "NamedArea", area: "content" }],
      },
      menu: { $type: "Stack", skins: [{ $type: "SplitterPaneSkin", size: "280px", min: "200px", max: "400px" }], areas: [] },
      content: { $type: "Stack", skins: [{ $type: "SplitterPaneSkin", size: "*" }], areas: [] },
    },
  };

  it("fixed-px pane gets a fixed flex-basis; star pane grows to fill", () => {
    const { container } = view(splitter);
    const panes = container.querySelectorAll<HTMLElement>("[data-splitter-pane]");
    expect(panes.length).toBe(2);
    const menuStyle = panes[0].getAttribute("style") ?? "";
    const contentStyle = panes[1].getAttribute("style") ?? "";
    // Fixed 280px pane: no grow, fixed basis.
    expect(menuStyle).toContain("flex: 0 0 280px");
    // Star pane: grows (grow >= 1), NOT pinned to 280px.
    expect(contentStyle).not.toContain("280px");
    expect(contentStyle).toMatch(/flex:\s*1\s+1\s+0%/);
  });
});
