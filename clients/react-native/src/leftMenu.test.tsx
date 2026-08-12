import { describe, expect, it, vi } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import { LeftMenuView, isSubmenuParent, GROUP_AREA } from "./leftMenu";
import { ThemeProvider } from "./theme";
import type { AreaTree } from "@meshweaver/react/core";

// Nested node-menu entries on MOBILE — a DRILL-DOWN, not the web clients' nested flyout.
//
// Deliberately a different interaction from Blazor / portal-next: a flyout opens a second panel
// beside the first, which needs hover and width; a phone has neither. Parity across clients means
// the same CAPABILITY (every nested entry is reachable), not the same gesture. What must NOT differ
// is that a grouping parent never acts — that holds on every client, and is pinned here too.
//
// Before this, the RN `MenuItem` shape had no `children` field at all, so a nested entry's whole
// subtree was invisible on mobile: the Export group would have rendered as a dead row.

type Json = ReactTestRendererJSON;

const NAV = { address: "Doc/Architecture", area: "Overview" };
const HOME = { address: "Doc/Architecture", area: "Overview" };
const CONTEXTS = [{ key: "$Menu:Node", label: "This node", glyph: "🧊" }];

function tree(items: unknown[]): AreaTree {
  return { areas: { "$Menu:Node": { items } } } as unknown as AreaTree;
}

function render(t: AreaTree, onNavigate = vi.fn()) {
  let r!: TestRenderer.ReactTestRenderer;
  TestRenderer.act(() => {
    r = TestRenderer.create(
      <ThemeProvider>
        <LeftMenuView
          tree={t}
          nav={NAV}
          home={HOME}
          contexts={CONTEXTS}
          clientMenus={[]}
          clientScreen={null}
          onNavigate={onNavigate}
          onClientScreen={() => {}}
        />
      </ThemeProvider>,
    );
  });
  return { r, onNavigate };
}

function textOf(node: Json | Json[] | null): string {
  if (node == null) return "";
  if (Array.isArray(node)) return node.map(textOf).join("");
  let out = "";
  for (const c of node.children ?? []) {
    if (typeof c === "string") out += c;
    else out += textOf(c as Json);
  }
  return out;
}

/**
 * The pressable row whose accessible label contains `needle` — looked up the way a screen reader
 * would, which also keeps the a11y labels honest: a row with no accessible name is unfindable here.
 */
function row(r: TestRenderer.ReactTestRenderer, needle: string) {
  const all = r.root.findAll(
    (n) =>
      typeof (n.props as { onPress?: unknown }).onPress === "function" &&
      typeof (n.props as { accessibilityLabel?: unknown }).accessibilityLabel === "string" &&
      // The Pressable itself, not the NavRow wrapper around it — only the Pressable carries the
      // role and the style we assert on.
      typeof (n.props as { accessibilityRole?: unknown }).accessibilityRole === "string",
    { deep: true },
  );
  const hit = all.find((n) => String(n.props.accessibilityLabel).includes(needle));
  if (!hit) throw new Error(`no row matching "${needle}"; have: ${all.map((n) => n.props.accessibilityLabel).join(" | ")}`);
  return hit;
}

const press = (r: TestRenderer.ReactTestRenderer, needle: string) =>
  TestRenderer.act(() => {
    (row(r, needle).props as { onPress: () => void }).onPress();
  });

const exportGroup = {
  label: "Export",
  area: GROUP_AREA,
  icon: "📦",
  order: 27,
  children: [
    { label: "Share as email", area: "SendDocument", icon: "📤", order: 29 },
    { label: "Export to PDF", area: "ExportPdf", icon: "📄", order: 27 },
    { label: "Export to DOCX", area: "ExportDocx", icon: "📝", order: 28 },
  ],
};

describe("isSubmenuParent (NodeMenuItemDefinition.IsSubmenuParent port)", () => {
  it("classifies leaves, children-carriers and the _group sentinel", () => {
    expect(isSubmenuParent({ label: "Edit", area: "Edit" })).toBe(false);
    expect(isSubmenuParent({ label: "P", area: "P", children: [{ label: "C", area: "C" }] })).toBe(true);
    expect(isSubmenuParent({ label: "Export", area: GROUP_AREA })).toBe(true);
    expect(isSubmenuParent({ label: "P", area: "P", children: [] })).toBe(false);
  });
});

describe("LeftMenuView — nested entries drill down", () => {
  it("shows the parent, and NOT its children, at the top level", () => {
    const { r } = render(tree([{ label: "Edit", area: "Edit", order: 10 }, exportGroup]));
    const text = textOf(r.toJSON() as Json);

    expect(text).toContain("Export");
    expect(text).not.toContain("Export to PDF");
  });

  it("marks a parent with a chevron so it reads as leading somewhere", () => {
    const { r } = render(tree([exportGroup]));
    expect(textOf(r.toJSON() as Json)).toContain("›");
  });

  it("drills IN on tap — children replace the list, the parent titles the view", () => {
    const { r } = render(tree([exportGroup]));
    press(r, "Export");

    const text = textOf(r.toJSON() as Json);
    expect(text).toContain("Export to PDF");
    expect(text).toContain("Back");
    // Exactly one level on screen: the surrounding top-level rows are gone.
    expect(text).not.toContain("Home");
  });

  it("never navigates when a grouping parent is tapped", () => {
    const onNavigate = vi.fn();
    const { r } = render(tree([exportGroup]), onNavigate);
    press(r, "Export");

    expect(onNavigate).not.toHaveBeenCalled();
  });

  it("navigates when a CHILD is tapped", () => {
    const onNavigate = vi.fn();
    const { r } = render(tree([exportGroup]), onNavigate);
    press(r, "Export");
    press(r, "Export to PDF");

    expect(onNavigate).toHaveBeenCalledWith({ address: NAV.address, area: "ExportPdf" });
  });

  it("backs out one level", () => {
    const { r } = render(tree([exportGroup]));
    press(r, "Export");
    press(r, "Back");

    const text = textOf(r.toJSON() as Json);
    expect(text).toContain("Home");
    expect(text).not.toContain("Export to PDF");
  });

  it("orders children by `order`, not by arrival", () => {
    const { r } = render(tree([exportGroup]));
    press(r, "Export");

    const text = textOf(r.toJSON() as Json);
    expect(text.indexOf("Export to PDF")).toBeLessThan(text.indexOf("Export to DOCX"));
    expect(text.indexOf("Export to DOCX")).toBeLessThan(text.indexOf("Share as email"));
  });

  it("gives a parent row a ≥44pt target, an accessible label and the menu role", () => {
    const { r } = render(tree([exportGroup]));
    const props = row(r, "Export").props as {
      accessibilityLabel?: string;
      accessibilityRole?: string;
      style?: unknown;
    };

    expect(props.accessibilityLabel).toBeTruthy();
    expect(props.accessibilityRole).toBe("menu");

    const style = typeof props.style === "function" ? (props.style as (s: unknown) => unknown)({ hovered: false }) : props.style;
    const flat = (Array.isArray(style) ? style.flat() : [style]).filter(Boolean) as Record<string, unknown>[];
    expect(flat.some((s) => (s as { minHeight?: number }).minHeight === 44)).toBe(true);
  });

  it("supports nesting deeper than one level", () => {
    const deep = {
      label: "L1",
      area: GROUP_AREA,
      order: 1,
      children: [{ label: "L2", area: GROUP_AREA, order: 1, children: [{ label: "L3", area: "L3Area", order: 1 }] }],
    };
    const { r } = render(tree([deep]));

    press(r, "L1");
    press(r, "L2");

    expect(textOf(r.toJSON() as Json)).toContain("L3");
  });
});
