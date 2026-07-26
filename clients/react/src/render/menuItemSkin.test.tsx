// MenuItemSkin parity with Blazor's MenuItemView.
//
// The regression this pins: MenuItemControl is a CONTAINER control that ALWAYS carries a
// MenuItemSkin (MenuItemControl.cs passes `new(Title, Icon)` to the base ctor), and ControlRenderer
// pops skins BEFORE it dispatches on $type. So on a live tree the leaf `MenuItem` control entry was
// never reached — the skin missed the registry, fell through to `__default` passthrough, and every
// nav item rendered as bare children with no title, no icon and no click. Title/icon live on the
// SKIN, not the control, which is why reading them off the control was not enough either.

/** @vitest-environment jsdom */
import { beforeAll, describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";

beforeAll(() => {
  if (!(globalThis as any).ResizeObserver)
    (globalThis as any).ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
});

/** A live-shaped menu item: the $type carries the C# class name and the skin holds title/icon. */
function menuTree(skin: Record<string, unknown>, extra: Record<string, unknown> = {}, children = false): AreaTree {
  return {
    data: {},
    areas: {
      main: {
        $type: "MenuItemControl",
        skins: [{ $type: "MenuItemSkin", title: "Documentation", icon: "Book", ...skin }],
        ...(children ? { areas: [{ area: "main/sub" }] } : {}),
        ...extra,
      },
      "main/sub": { $type: "Label", data: "Getting started" },
    },
  };
}

describe("MenuItem skin (Blazor MenuItemView parity)", () => {
  it("renders the skin's title — not passthrough children", () => {
    render(<MeshAreaView source={new StaticAreaSource(menuTree({}))} rootArea="main" />);
    expect(screen.getByRole("button", { name: /Documentation/ })).toBeTruthy();
  });

  it("fires the control's click when clickable", () => {
    const source = new StaticAreaSource(menuTree({}, { isClickable: true }));
    render(<MeshAreaView source={source} rootArea="main" />);
    fireEvent.click(screen.getByRole("button", { name: /Documentation/ }));
    expect(source.events.map((e) => e.kind)).toContain("click");
  });

  it("iconOnly drops the label (Blazor's icon-only mode)", () => {
    render(<MeshAreaView source={new StaticAreaSource(menuTree({ iconOnly: true }))} rootArea="main" />);
    expect(screen.queryByText("Documentation")).toBeNull();
  });

  it("expands its child areas on click when it has sub-menus", () => {
    render(<MeshAreaView source={new StaticAreaSource(menuTree({}, {}, true))} rootArea="main" />);
    // Collapsed initially — Blazor's dropdown only renders while isMenuOpen.
    expect(screen.queryByText("Getting started")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /Documentation/ }));
    expect(screen.getByText("Getting started")).toBeTruthy();
  });
});
