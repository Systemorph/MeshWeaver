import { beforeAll, describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl } from "../core.js";

// Rendering-fidelity parity: mount ONE of every "leaf" control (the ones that don't need a live mesh
// connection) through the real Fluent pack and prove each actually renders — no throw, and none fall
// through to the "Unsupported control" fallback. This is a step beyond the registry check in parity.test.ts:
// it catches a control that is registered but crashes on mount or renders nothing.
//
// Data/mesh controls (DataGrid, Chart, MeshSearch, ThreadChat, UserProfile, editors, …) need a live
// AreaSource/hub and are covered by the registry-coverage test instead. Dialog is a portal-rendered
// modal with its own test (controls/dialog.test.tsx); ItemTemplate's binding semantics are pinned in
// controls/itemTemplate.test.tsx and the theme panel (Appearance) in theme/theme.test.tsx.

// jsdom lacks the browser APIs several Fluent components probe on mount.
beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = (q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null }) as unknown as MediaQueryList;
  if (!(globalThis as any).ResizeObserver)
    (globalThis as any).ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
});

const ref = (area: string) => ({ $type: "NamedArea", area });
const ptr = (pointer: string) => ({ $type: "JsonPointerReference", pointer });
const opts = [
  { value: "a", text: "Alpha" },
  { value: "b", text: "Beta" },
];

const leaves: Record<string, UiControl> = {
  label: { $type: "Label", data: "Hello world", typo: "Header" },
  badge: { $type: "Badge", data: "New" },
  icon: { $type: "Icon", data: "Add" },
  html: { $type: "Html", data: "<b>bold</b>" },
  markdown: { $type: "Markdown", data: "# Heading\n\ntext" },
  codeSample: { $type: "CodeSample", data: "const x = 1;" },
  highlight: { $type: "Highlight", data: "const y = 2;" },
  exception: { $type: "Exception", message: "boom", type: "InvalidOperation" },
  spacer: { $type: "Spacer" },
  button: { $type: "Button", data: "Click me", isClickable: true },
  checkbox: { $type: "CheckBox", label: "Enabled", data: ptr("/data/flag") },
  toggle: { $type: "Switch", label: "On", data: ptr("/data/flag") },
  slider: { $type: "Slider", data: ptr("/data/num"), min: 0, max: 100 },
  date: { $type: "Date", data: "2026-01-01" },
  dateTime: { $type: "DateTime", data: "2026-01-01T10:00:00" },
  select: { $type: "Select", data: ptr("/data/choice"), options: opts },
  combobox: { $type: "Combobox", data: ptr("/data/choice"), options: opts },
  listbox: { $type: "Listbox", data: ptr("/data/choice"), options: opts },
  radioGroup: { $type: "RadioGroup", data: ptr("/data/choice"), options: opts },
  textArea: { $type: "TextArea", label: "Notes", data: ptr("/data/txt") },
  numberField: { $type: "NumberField", label: "Count", data: ptr("/data/num") },
  menuItem: { $type: "MenuItem", title: "An item" },
  searchBox: { $type: "SearchBox", data: ptr("/data/txt") },
  navLink: { $type: "NavLink", title: "Home", url: "/" },
  progress: { $type: "Progress", data: 0.5 },
  spinner: { $type: "Spinner" },
  appearance: { $type: "Appearance" }, // the theme settings panel (needs no mesh — localStorage only)
  itemTemplate: { $type: "ItemTemplate", data: ptr("/data/people"), view: { $type: "Label", data: ptr("name") } },
  layoutAreaDefinition: {
    $type: "LayoutAreaDefinition",
    definition: { area: "Overview", url: "/app/Overview", title: "Overview", description: "The overview area" },
  },
};

const LEAVES = Object.keys(leaves);

const gallery: AreaTree = {
  data: { flag: true, num: 42, txt: "hello", choice: "a", people: [{ name: "Ada" }, { name: "Grace" }] },
  areas: {
    main: { $type: "Stack", skins: [{ $type: "LayoutStack" }], areas: LEAVES.map(ref) },
    ...leaves,
  },
};

describe("Fluent pack renders every safe control (no fallback, no throw)", () => {
  it(`mounts a gallery of ${LEAVES.length} controls`, () => {
    const { container } = render(<MeshAreaView source={new StaticAreaSource(gallery)} rootArea="main" />);
    // None fell through to the fallback (which prints "Unsupported control: <type>").
    expect(container.textContent).not.toContain("Unsupported control");
    // Real content rendered.
    expect(container.textContent).toContain("Hello world"); // the Label
    expect(container.textContent).toContain("Ada"); // the ItemTemplate repeated its view per item
    expect(container.querySelectorAll("*").length).toBeGreaterThan(LEAVES.length);
  });
});
