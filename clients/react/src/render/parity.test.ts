import { describe, expect, it } from "vitest";
import { controlRegistry } from "./registry.js";
import { skinRegistry } from "./skins.js";

// Feature-parity guard: the Fluent React pack must render every control the Blazor portal renders.
// These lists are the authoritative Blazor vocabulary — the `*Control` / `*Skin` types in
// src/MeshWeaver.Layout (the `$type` is the class name minus the "Control"/"Skin" suffix). When a new
// control is added to Layout, add its $type here; this test then fails until the React pack covers it,
// keeping the port at 1:1 with Blazor. Containers (Stack/Layout/Tabs/…) render via SKINS, so they live in
// the skin list, not the control list; DataGrid columns (Property/Template/Data) render inside DataGrid.

const BLAZOR_LEAF_CONTROLS = [
  "Appearance", "Badge", "Button", "Catalog", "Chart", "CheckBox", "CodeEditor", "CodeSample",
  "CollaborativeMarkdown", "Combobox", "DataGrid", "Date", "DateTime", "Dialog", "DiffEditor",
  "DocumentSource", "Exception", "ExportDocument", "FileBrowser", "Highlight", "Html", "Icon",
  "ItemTemplate", "Label", "LayoutArea", "LayoutAreaDefinition", "Listbox", "Markdown", "MarkdownEditor",
  "MenuItem", "MeshNodeCollection", "MeshNodePicker", "MeshSearch", "NamedArea", "NavLink", "NodeExport",
  "NodeImport", "NumberField", "PivotGrid", "Progress", "RadioGroup", "Redirect", "SearchBox", "Select",
  "Slider", "Spacer", "Switch", "TextArea", "ThreadChat", "ThreadMessageBubble", "UserProfile",
];

// Container + item skins the portal renders (the `*Skin` types + the container dispatch).
const BLAZOR_SKINS = [
  "LayoutStack", "Layout", "LayoutGrid", "LayoutGridItem", "Card", "Splitter", "Tabs", "Tab", "Toolbar",
  "NavMenu", "NavGroup", "Header", "Footer", "Main", "BodyContent", "Property", "EditForm", "Editor",
];

describe("React ↔ Blazor control parity", () => {
  it("the Fluent pack registers every Blazor leaf control $type", () => {
    const missing = BLAZOR_LEAF_CONTROLS.filter((t) => !(t in controlRegistry));
    expect(missing, `Missing Blazor controls in the React pack: ${missing.join(", ")}`).toEqual([]);
  });

  it("the Fluent pack registers every Blazor skin $type", () => {
    const missing = BLAZOR_SKINS.filter((t) => !(t in skinRegistry));
    expect(missing, `Missing Blazor skins in the React pack: ${missing.join(", ")}`).toEqual([]);
  });

  it("registered controls are React components (functions), not accidental values", () => {
    const bad = BLAZOR_LEAF_CONTROLS.filter((t) => t in controlRegistry && typeof controlRegistry[t] !== "function");
    expect(bad, `Non-component control entries: ${bad.join(", ")}`).toEqual([]);
  });

  it("covers Blazor's full leaf set (no silent shortfall)", () => {
    const covered = BLAZOR_LEAF_CONTROLS.filter((t) => t in controlRegistry).length;
    expect(covered).toBe(BLAZOR_LEAF_CONTROLS.length); // 51/51 — full $type parity
  });
});
