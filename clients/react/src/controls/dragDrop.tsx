import type { ReactNode } from "react";
import type { Json, UiControl } from "../area/types.js";
import { useEmit, useScope } from "../area/context.js";
import { RenderArea } from "../render/ControlRenderer.js";

// A browser has exactly one active HTML5 drag at a time, so a module-level slot faithfully models
// "the payload in flight". Reading it on drop keeps the drop robust even when a synthetic drag (a
// Playwright `dragTo`) does not carry the DataTransfer; the DataTransfer is still set on dragstart
// for native drag behaviour and accessibility.
let activeDragPayload: Json = undefined;

/** The `{area}/Content` sub-area a Draggable/DropTarget renders its wrapped control into. */
function contentAreaKey(control: UiControl): string | undefined {
  const area = (control.contentArea as UiControl | undefined)?.area;
  return area != null ? String(area) : undefined;
}

function DraggableView({ control }: { control: UiControl }): ReactNode {
  const payload = control.payload as Json;
  const area = contentAreaKey(control);
  return (
    <div
      draggable
      data-draggable={payload == null ? "" : String(payload)}
      style={{ cursor: "grab" }}
      onDragStart={(e) => {
        activeDragPayload = payload;
        try {
          e.dataTransfer.setData("text/plain", payload == null ? "" : String(payload));
          e.dataTransfer.effectAllowed = "move";
        } catch {
          /* jsdom has no DataTransfer — the module-level slot still carries the payload */
        }
      }}
      onDragEnd={() => {
        activeDragPayload = undefined;
      }}
    >
      {area ? <RenderArea areaKey={area} /> : null}
    </div>
  );
}

function DropTargetView({ control }: { control: UiControl }): ReactNode {
  const emit = useEmit();
  const { area: dropArea } = useScope();
  const area = contentAreaKey(control);
  return (
    <div
      data-drop-target=""
      onDragOver={(e) => {
        e.preventDefault();
        try {
          e.dataTransfer.dropEffect = "move";
        } catch {
          /* ignore in jsdom */
        }
      }}
      onDrop={(e) => {
        e.preventDefault();
        let payload = activeDragPayload;
        if (payload === undefined) {
          try {
            payload = e.dataTransfer.getData("text/plain");
          } catch {
            /* ignore */
          }
        }
        emit({ kind: "drop", area: dropArea, value: payload });
      }}
    >
      {area ? <RenderArea areaKey={area} /> : null}
    </div>
  );
}

export const dragDropControls = {
  Draggable: DraggableView,
  DropTarget: DropTargetView,
};
