// ItemTemplate parity with Blazor's Components/ItemTemplate.razor: the `view` template renders once
// per item of the bound `data` collection, each instance data-contexted to "{dataPointer}/{i}" so
// RELATIVE template bindings read from — and write back to — that item.

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

const ptr = (pointer: string) => ({ $type: "JsonPointerReference", pointer });

function tree(): AreaTree {
  return {
    data: {
      people: [
        { name: "Ada", role: "Engineer" },
        { name: "Grace", role: "Admiral" },
        { name: "Edsger", role: "Professor" },
      ],
    },
    areas: {
      main: {
        $type: "ItemTemplate",
        data: ptr("/data/people"),
        view: { $type: "Label", data: ptr("name") }, // relative binding → the item's field
      },
    },
  };
}

describe("ItemTemplate — the repeater", () => {
  it("renders the view once per item with per-item relative bindings", () => {
    const { container } = render(<MeshAreaView source={new StaticAreaSource(tree())} rootArea="main" />);
    expect(container.textContent).toContain("Ada");
    expect(container.textContent).toContain("Grace");
    expect(container.textContent).toContain("Edsger");
    expect(container.textContent).not.toContain("Unsupported control");
  });

  it("template edits write back to the ITEM's pointer (dataContext scoping)", () => {
    const source = new StaticAreaSource({
      ...tree(),
      areas: {
        main: {
          $type: "ItemTemplate",
          data: ptr("/data/people"),
          view: { $type: "TextField", data: ptr("name") },
        },
      },
    });
    render(<MeshAreaView source={source} rootArea="main" />);
    const inputs = screen.getAllByRole("textbox") as HTMLInputElement[];
    expect(inputs.map((i) => i.value)).toEqual(["Ada", "Grace", "Edsger"]);

    fireEvent.change(inputs[1], { target: { value: "Grace Hopper" } });
    const update = source.events.find((e) => e.kind === "update");
    expect(update?.pointer).toBe("/data/people/1/name");
    expect(update?.value).toBe("Grace Hopper");
    expect((source.getState().data as any).people[1].name).toBe("Grace Hopper");
    // The other items were untouched.
    expect((source.getState().data as any).people[0].name).toBe("Ada");
  });

  it("lays out horizontally when orientation says so (Blazor Orientation.Horizontal = 0)", () => {
    const t = tree();
    (t.areas!.main as any).orientation = 0;
    const { container } = render(<MeshAreaView source={new StaticAreaSource(t)} rootArea="main" />);
    const row = Array.from(container.querySelectorAll("div")).find((d) => d.style.flexDirection === "row");
    expect(row).toBeTruthy();
  });

  it("renders nothing gracefully for an empty or unbound collection", () => {
    const t = tree();
    (t.data as any).people = [];
    const { container } = render(<MeshAreaView source={new StaticAreaSource(t)} rootArea="main" />);
    expect(container.textContent).not.toContain("Unsupported control");
  });
});
