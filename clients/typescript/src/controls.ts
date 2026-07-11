// Layout drag-and-drop wire model. The generic Draggable / DropTarget controls and the DropEvent a
// drop posts back to the layout hub — the same wire shapes the C# LayoutAreaHost produces and
// consumes ($type discriminators, camelCase fields). Lets a Node/Bun client construct draggable
// trees and read drop events natively, the drag-drop twin of `meshNodeFromChange`.

/** A NamedArea reference — the sub-area a container renders a wrapped child control into. */
export interface NamedArea {
  $type: "NamedArea";
  area: string;
}

/** A generic drag source wrapping a child control (via `contentArea`), carrying a `payload`. */
export interface DraggableControl {
  $type: "DraggableControl";
  payload?: unknown;
  contentArea?: NamedArea;
}

/** A generic drop target wrapping a child control (via `contentArea`). */
export interface DropTargetControl {
  $type: "DropTargetControl";
  contentArea?: NamedArea;
}

/** The event a drop target posts to the layout hub when a draggable is dropped on it. */
export interface DropEvent {
  $type: "DropEvent";
  area: string;
  streamId: string;
  payload?: unknown;
}

/** Build a DropEvent wire object addressed to `area` on `streamId`, carrying `payload`. */
export function dropEvent(area: string, streamId: string, payload?: unknown): DropEvent {
  return { $type: "DropEvent", area, streamId, payload };
}

/**
 * Decode a DropEvent from a wire message, tolerant of Pascal/camel casing (the C# hub emits
 * camelCase, but a raw envelope may carry either), mirroring `meshNodeFromChange`.
 */
export function dropEventFromMessage(message: Record<string, unknown>): DropEvent {
  const g = (...keys: string[]): unknown => {
    for (const k of keys) if (k in message) return message[k];
    return undefined;
  };
  return {
    $type: "DropEvent",
    area: (g("area", "Area") as string) ?? "",
    streamId: (g("streamId", "StreamId") as string) ?? "",
    payload: g("payload", "Payload"),
  };
}
