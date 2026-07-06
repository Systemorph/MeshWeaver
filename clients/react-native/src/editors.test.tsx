import { describe, expect, it } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource, type AreaTree } from "@meshweaver/react/core";
import { rnPack } from "./rnPack";

// Headless proof that EVERY editor the RN leaf pack adds actually renders to native components AND
// resolves its /data binding — the runtime counterpart to `tsc`. Mirrors rnPack.test.tsx's harness.

const ref = (area: string) => ({ $type: "NamedArea", area });
const ptr = (pointer: string) => ({ $type: "JsonPointerReference", pointer });

const tree: AreaTree = {
  data: {
    age: 42,
    color: "green",
    size: "M",
    volume: 30,
    when: "2026-07-06T14:30:00Z",
    city: "Zurich",
    query: "",
    code: "const x = 1;\nconsole.log(x);",
    md: "# Notes\n\nbody",
  },
  areas: {
    main: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", verticalGap: 8 }],
      areas: [
        ref("num"), ref("sel"), ref("radio"), ref("slider"), ref("date"), ref("dt"),
        ref("combo"), ref("search"), ref("code"), ref("mdedit"), ref("diff"),
        ref("sample"), ref("err"), ref("menu"), ref("form"),
      ],
    },
    num: { $type: "NumberField", label: "Age", data: ptr("/data/age") },
    sel: { $type: "Select", label: "Color", data: ptr("/data/color"), options: [{ value: "red", text: "Red" }, { value: "green", text: "Green" }] },
    radio: { $type: "RadioGroup", label: "Size", data: ptr("/data/size"), options: [{ value: "S", text: "Small" }, { value: "M", text: "Medium" }] },
    slider: { $type: "Slider", label: "Volume", data: ptr("/data/volume"), min: 0, max: 100, step: 5 },
    date: { $type: "Date", label: "When", data: ptr("/data/when") },
    dt: { $type: "DateTime", label: "When exactly", data: ptr("/data/when") },
    combo: { $type: "Combobox", label: "City", data: ptr("/data/city"), options: [{ value: "Zurich", text: "Zurich" }, { value: "Bern", text: "Bern" }] },
    search: { $type: "SearchBox", data: ptr("/data/query"), placeholder: "Find" },
    code: { $type: "CodeEditor", label: "Code", data: ptr("/data/code") },
    mdedit: { $type: "MarkdownEditor", label: "Notes", data: ptr("/data/md") },
    diff: { $type: "DiffEditor", original: "old line", data: "new line" },
    sample: { $type: "CodeSample", data: "readonly();" },
    err: { $type: "Exception", message: "Boom happened" },
    menu: { $type: "MenuItem", title: "Open", isClickable: true },
    form: { $type: "EditForm", areas: [ref("num")] },
  },
};

type Json = ReactTestRendererJSON;

function render(): Json {
  let root!: TestRenderer.ReactTestRenderer;
  TestRenderer.act(() => {
    root = TestRenderer.create(
      <RegistryProvider pack={rnPack}>
        <ScopeProvider source={new StaticAreaSource(tree)} area="main">
          <RenderArea areaKey="main" />
        </ScopeProvider>
      </RegistryProvider>,
    );
  });
  return root.toJSON() as Json;
}

function* walk(node: Json | Json[] | null): Generator<Json> {
  if (node == null) return;
  if (Array.isArray(node)) { for (const n of node) yield* walk(n); return; }
  yield node;
  if (node.children) for (const c of node.children) yield* walk(c as Json);
}

function textOf(node: Json): string {
  let out = "";
  for (const c of node.children ?? []) {
    if (typeof c === "string") out += c;
    else if (c && typeof c === "object") out += textOf(c as Json);
  }
  return out;
}

describe("RN leaf pack renders every editor + resolves its binding", () => {
  const nodes = [...walk(render())];
  const byType = (t: string) => nodes.filter((n) => n.type === t);
  const inputValues = byType("TextInput").map((n) => String(n.props.value ?? ""));
  const allText = byType("Text").map(textOf);
  const hasText = (needle: string) => allText.some((t) => t.includes(needle));

  it("never hits the Unsupported fallback", () => {
    expect(hasText("Unsupported")).toBe(false);
  });

  it("NumberField binds the numeric value into a TextInput", () => {
    expect(inputValues).toContain("42");
  });

  it("Select renders options and ticks the bound one", () => {
    expect(hasText("Red")).toBe(true);
    expect(hasText("Green")).toBe(true);
    expect(hasText("✓")).toBe(true); // "green" is selected
  });

  it("RadioGroup renders its option labels", () => {
    expect(hasText("Small")).toBe(true);
    expect(hasText("Medium")).toBe(true);
  });

  it("Slider shows the bound value", () => {
    expect(hasText("30")).toBe(true);
  });

  it("Date / DateTime slice the ISO value to the right width", () => {
    expect(inputValues).toContain("2026-07-06"); // Date → 10 chars
    expect(inputValues).toContain("2026-07-06T14:30"); // DateTime → 16 chars
  });

  it("Combobox binds the freeform value", () => {
    expect(inputValues).toContain("Zurich");
  });

  it("CodeEditor + MarkdownEditor bind their text into editable TextInputs", () => {
    expect(inputValues.some((v) => v.includes("console.log(x)"))).toBe(true);
    expect(inputValues.some((v) => v.includes("# Notes"))).toBe(true);
  });

  it("DiffEditor shows original and modified", () => {
    expect(hasText("old line")).toBe(true);
    expect(hasText("new line")).toBe(true);
  });

  it("CodeSample + Exception + MenuItem render their content", () => {
    expect(hasText("readonly();")).toBe(true);
    expect(hasText("Boom happened")).toBe(true);
    expect(hasText("Open")).toBe(true);
  });
});
