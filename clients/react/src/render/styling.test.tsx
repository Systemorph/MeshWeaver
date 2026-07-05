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
