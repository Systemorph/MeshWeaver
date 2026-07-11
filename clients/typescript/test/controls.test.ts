import { describe, expect, it } from "vitest";
import {
  dropEvent,
  dropEventFromMessage,
  type DraggableControl,
  type DropTargetControl,
} from "../src/controls.js";

describe("drag-and-drop wire model", () => {
  it("builds a DropEvent with the $type discriminator and camelCase fields", () => {
    const e = dropEvent("zone", "stream-1", "card-1");
    expect(e).toEqual({ $type: "DropEvent", area: "zone", streamId: "stream-1", payload: "card-1" });
  });

  it("round-trips a DropEvent through JSON", () => {
    const e = dropEvent("zone", "stream-1", { id: "card-1" });
    const back = dropEventFromMessage(JSON.parse(JSON.stringify(e)) as Record<string, unknown>);
    expect(back).toEqual(e);
  });

  it("decodes a Pascal-cased wire message (server casing tolerance)", () => {
    const back = dropEventFromMessage({ $type: "DropEvent", Area: "zone", StreamId: "s1", Payload: "card-1" });
    expect(back.area).toBe("zone");
    expect(back.streamId).toBe("s1");
    expect(back.payload).toBe("card-1");
  });

  it("types Draggable / DropTarget controls with contentArea references that round-trip", () => {
    const drag: DraggableControl = {
      $type: "DraggableControl",
      payload: "card-1",
      contentArea: { $type: "NamedArea", area: "card/Content" },
    };
    const drop: DropTargetControl = {
      $type: "DropTargetControl",
      contentArea: { $type: "NamedArea", area: "zone/Content" },
    };
    expect(JSON.parse(JSON.stringify(drag)).$type).toBe("DraggableControl");
    expect(JSON.parse(JSON.stringify(drop)).contentArea.area).toBe("zone/Content");
  });
});
