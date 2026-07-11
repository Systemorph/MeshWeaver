import { StaticAreaSource, type AreaTree } from "@meshweaver/react/core";

const ref = (area: string) => ({ $type: "NamedArea", area });
const ptr = (pointer: string) => ({ $type: "JsonPointerReference", pointer });

// A sample area exercising the RN leaf pack — same {areas,data} shape the mesh streams and the web
// demo uses; only the leaf components differ.
export const sampleArea: AreaTree = {
  data: {
    name: "Ada Lovelace",
    active: true,
    rows: [
      { name: "ACME", amount: 124000 },
      { name: "Northwind", amount: 98230 },
    ],
  },
  areas: {
    main: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", verticalGap: 16 }],
      areas: [ref("title"), ref("intro"), ref("card"), ref("dnd"), ref("grid"), ref("footer")],
    },
    title: { $type: "Label", typo: "Header", data: "MeshWeaver on React Native" },
    intro: { $type: "Markdown", data: "The same UiControl tree as Blazor, MAUI and the web — native leaves." },
    card: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("name"), ref("active"), ref("saveBtn"), ref("status")] },
    name: { $type: "TextField", label: "Name", data: ptr("/data/name") },
    active: { $type: "CheckBox", label: "Active", data: ptr("/data/active") },
    saveBtn: { $type: "Button", data: "Save", isClickable: true },
    status: { $type: "Badge", data: "Green" },
    grid: {
      $type: "DataGrid",
      data: ptr("/data/rows"),
      columns: [
        { $type: "PropertyColumn", property: "name", title: "Account" },
        { $type: "PropertyColumn", property: "amount", title: "Amount" },
      ],
    },
    dnd: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("dndTitle"), ref("dndRow")] },
    dndTitle: { $type: "Label", data: "Drag & drop" },
    dndRow: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", orientation: "Horizontal", horizontalGap: 12 }],
      areas: [ref("dragCard"), ref("dropZone")],
    },
    dragCard: { $type: "Draggable", payload: "card-1", contentArea: ref("dragCardContent") },
    dragCardContent: { $type: "Label", data: "Drag me" },
    dropZone: { $type: "DropTarget", contentArea: ref("dropZoneContent") },
    dropZoneContent: { $type: "Label", data: "Drop here" },

    footer: { $type: "Markdown", data: "Rendered by the RN leaf pack." },
  },
};

/**
 * The offline demo source. It records emitted events (like the web demo) and, on a "drop", reflects
 * the dropped payload into the drop zone's label — a self-contained, observable drag-and-drop the
 * Playwright e2e drives without a live mesh.
 */
export function createSampleSource(): StaticAreaSource {
  const source = new StaticAreaSource(sampleArea);
  const emit = source.emit;
  source.emit = (event) => {
    emit(event);
    if (event.kind === "drop")
      source.applyPatch({ areas: { dropZoneContent: { data: `Dropped: ${event.value ?? ""}` } } });
  };
  return source;
}
