// Dialog parity with Blazor's DialogView (FluentDialog): a REAL modal — shown on mount, title in
// the header, ContentArea in the body, ActionsArea in the footer when hasActions (else Close when
// isClosable) — and dismissal posts a CloseDialogEvent (the "closeDialog" MeshEvent) exactly like
// Blazor's HandleClose posts CloseDialogEvent(Area, StreamId, DialogCloseState).

import { beforeAll, describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";

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

function dialogTree(overrides: Record<string, unknown> = {}): AreaTree {
  return {
    data: {},
    areas: {
      main: {
        $type: "Dialog",
        title: "Confirm action",
        isClosable: true,
        contentArea: { $type: "NamedArea", area: "$Dialog/ContentArea" },
        actionsArea: { $type: "NamedArea", area: "$Dialog/ActionsArea" },
        ...overrides,
      },
      "$Dialog/ContentArea": { $type: "Label", data: "Are you sure?" },
      "$Dialog/ActionsArea": { $type: "Button", data: "Create", isClickable: true },
    },
  };
}

describe("Dialog — a real modal (Blazor DialogView parity)", () => {
  it("opens as a modal with title and content area", () => {
    render(<MeshAreaView source={new StaticAreaSource(dialogTree())} rootArea="main" />);
    const dialog = screen.getByRole("dialog"); // rendered in a portal, not inline
    expect(dialog.textContent).toContain("Confirm action");
    expect(dialog.textContent).toContain("Are you sure?");
    // No actions set → the closable footer shows the default Close button.
    expect(screen.getByRole("button", { name: "Close" })).toBeTruthy();
  });

  it("renders the ActionsArea in the footer when hasActions (replacing Close)", () => {
    render(<MeshAreaView source={new StaticAreaSource(dialogTree({ hasActions: true }))} rootArea="main" />);
    expect(screen.getByRole("button", { name: "Create" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Close" })).toBeNull();
  });

  it("Close dismisses the dialog and emits the CloseDialogEvent (OK)", () => {
    const source = new StaticAreaSource(dialogTree());
    render(<MeshAreaView source={source} rootArea="main" />);
    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    const close = source.events.find((e) => e.kind === "closeDialog");
    expect(close?.area).toBe("main");
    expect(close?.value).toBe("OK");
  });

  it("applies Blazor's size widths (S=400px, M=600px, L=800px)", () => {
    render(<MeshAreaView source={new StaticAreaSource(dialogTree({ size: "L" }))} rootArea="main" />);
    const surface = document.querySelector(".fui-DialogSurface") as HTMLElement;
    expect(surface?.style.maxWidth).toBe("800px");
  });
});
