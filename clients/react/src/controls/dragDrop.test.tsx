// Draggable / DropTarget parity with the Blazor DraggableView / DropTargetView: each wraps a child
// control rendered via its `contentArea` NamedArea (like Dialog's ContentArea), and a drop posts the
// "drop" MeshEvent carrying the dragged payload to the drop target's area — exactly what the server's
// LayoutAreaHost.OnDrop consumes to invoke the DropTargetControl's DropAction.

import { describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";

function boardTree(): AreaTree {
  return {
    data: {},
    areas: {
      main: {
        $type: "Stack",
        skins: [{ $type: "LayoutStack" }],
        areas: [
          { $type: "NamedArea", area: "card" },
          { $type: "NamedArea", area: "zone" },
        ],
      },
      card: {
        $type: "Draggable",
        payload: "card-1",
        contentArea: { $type: "NamedArea", area: "card/Content" },
      },
      "card/Content": { $type: "Label", data: "Design API" },
      zone: {
        $type: "DropTarget",
        contentArea: { $type: "NamedArea", area: "zone/Content" },
      },
      "zone/Content": { $type: "Label", data: "Done" },
    },
  };
}

describe("Draggable / DropTarget", () => {
  it("renders the wrapped content of both the draggable and the drop target", () => {
    render(<MeshAreaView source={new StaticAreaSource(boardTree())} rootArea="main" />);
    expect(screen.getByText("Design API")).toBeTruthy();
    expect(screen.getByText("Done")).toBeTruthy();
    // The draggable carries its payload as a data attribute (drag source + e2e handle).
    expect(document.querySelector('[data-draggable="card-1"]')).toBeTruthy();
    expect(document.querySelector("[data-drop-target]")).toBeTruthy();
  });

  it("emits a drop event carrying the dragged payload to the target's area", () => {
    const source = new StaticAreaSource(boardTree());
    render(<MeshAreaView source={source} rootArea="main" />);

    const draggable = document.querySelector('[data-draggable="card-1"]') as HTMLElement;
    const dropZone = document.querySelector("[data-drop-target]") as HTMLElement;

    fireEvent.dragStart(draggable);
    fireEvent.dragOver(dropZone);
    fireEvent.drop(dropZone);

    const drop = source.events.find((e) => e.kind === "drop");
    expect(drop).toBeTruthy();
    expect(drop?.area).toBe("zone");
    expect(drop?.value).toBe("card-1");
  });

  it("does not emit a drop merely from starting a drag (only the target handles drop)", () => {
    const source = new StaticAreaSource(boardTree());
    render(<MeshAreaView source={source} rootArea="main" />);

    fireEvent.dragStart(document.querySelector('[data-draggable="card-1"]') as HTMLElement);

    expect(source.events.some((e) => e.kind === "drop")).toBe(false);
  });
});
