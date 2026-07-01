// LayoutAreaControl parity — the REAL nested-area embed. A LayoutAreaControl references another
// area stream ((address, LayoutAreaReference)); with an AreaSourceFactory installed
// (EmbeddedAreaProvider) the control opens a nested AreaSource and renders the referenced tree in
// its own scope — the React mirror of Blazor's LayoutAreaView second synchronization stream. This
// is what makes doc pages and composite views work.

import { beforeAll, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MeshAreaView, EmbeddedAreaProvider, type AreaSourceFactory } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl } from "../core.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
  if (!(globalThis as { ResizeObserver?: unknown }).ResizeObserver)
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
});

function outerTree(reference: Record<string, unknown>): AreaTree {
  return {
    data: {},
    areas: {
      main: { $type: "LayoutArea", address: "app/docs", reference } as unknown as UiControl,
    },
  };
}

describe("LayoutArea — nested area embedding", () => {
  it("opens a nested source via the factory and renders the referenced area's tree", async () => {
    const nested = new StaticAreaSource({
      data: { greeting: "Hello from the nested area" },
      areas: {
        Overview: { $type: "Label", data: { $type: "JsonPointerReference", pointer: "/data/greeting" } },
      },
    });
    const dispose = vi.fn();
    const factory: AreaSourceFactory = vi.fn((address, ref) => ({ source: nested, rootArea: ref.area ?? "", dispose }));

    const { unmount } = render(
      <EmbeddedAreaProvider factory={factory}>
        <MeshAreaView source={new StaticAreaSource(outerTree({ area: "Overview", id: "42" }))} rootArea="main" />
      </EmbeddedAreaProvider>,
    );

    expect(await screen.findByText("Hello from the nested area")).toBeTruthy();
    expect(factory).toHaveBeenCalledWith("app/docs", { area: "Overview", id: "42", layout: undefined });

    // Unmounting the embed closes the nested subscription.
    unmount();
    expect(dispose).toHaveBeenCalledTimes(1);
  });

  it("shows the loading spinner until the referenced area arrives, then swaps in the content", async () => {
    const nested = new StaticAreaSource({ data: {}, areas: {} });
    const factory: AreaSourceFactory = () => ({ source: nested, rootArea: "Overview" });
    render(
      <EmbeddedAreaProvider factory={factory}>
        <MeshAreaView source={new StaticAreaSource(outerTree({ area: "Overview" }))} rootArea="main" />
      </EmbeddedAreaProvider>,
    );
    expect(await screen.findByRole("progressbar")).toBeTruthy();

    // The nested stream's first frame lands → the spinner yields to the real tree.
    nested.applyPatch({ areas: { Overview: { $type: "Label", data: "Loaded!" } } });
    expect(await screen.findByText("Loaded!")).toBeTruthy();
    expect(screen.queryByRole("progressbar")).toBeNull();
  });

  it("follows the empty-reference default-area indirection (areas[''] NamedArea)", async () => {
    // An empty reference.area subscribes the target's DEFAULT area: the first frame stores the
    // indirection under the empty key (grpcSource wire contract).
    const nested = new StaticAreaSource({
      data: {},
      areas: {
        "": { $type: "NamedArea", area: "Resolved" },
        Resolved: { $type: "Label", data: "Default area content" },
      },
    });
    const factory: AreaSourceFactory = (_, ref) => ({ source: nested, rootArea: ref.area ?? "" });
    render(
      <EmbeddedAreaProvider factory={factory}>
        <MeshAreaView source={new StaticAreaSource(outerTree({}))} rootArea="main" />
      </EmbeddedAreaProvider>,
    );
    expect(await screen.findByText("Default area content")).toBeTruthy();
  });

  it("falls back to the informative marker when no factory is installed (static demos)", () => {
    render(<MeshAreaView source={new StaticAreaSource(outerTree({ area: "Overview" }))} rootArea="main" />);
    expect(screen.getByText(/Embedded layout area/)).toBeTruthy();
    expect(screen.getByText("Overview")).toBeTruthy();
  });
});
