import { describe, expect, it } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource } from "@meshweaver/react/core";
import { rnPack } from "./rnPack";
import { sampleArea } from "./sample";

// Render the sample area through the RN leaf pack + the shared renderer core, headless. Proves the app
// actually renders a UiControl tree to native components and resolves /data bindings — the runtime proof
// that "typecheck passes" can't give. react-native is mocked to identifiable host nodes (see vitest.config).

type Json = ReactTestRendererJSON;

function render(): Json {
  let root!: TestRenderer.ReactTestRenderer;
  TestRenderer.act(() => {
    root = TestRenderer.create(
      <RegistryProvider pack={rnPack}>
        <ScopeProvider source={new StaticAreaSource(sampleArea)} area="main">
          <RenderArea areaKey="main" />
        </ScopeProvider>
      </RegistryProvider>,
    );
  });
  return root.toJSON() as Json;
}

// Depth-first walk yielding every node in the rendered tree.
function* walk(node: Json | Json[] | null): Generator<Json> {
  if (node == null) return;
  if (Array.isArray(node)) {
    for (const n of node) yield* walk(n);
    return;
  }
  yield node;
  if (node.children) for (const c of node.children) yield* walk(c as Json);
}

// The visible text under a node (its string children, recursively).
function textOf(node: Json): string {
  let out = "";
  for (const c of node.children ?? []) {
    if (typeof c === "string") out += c;
    else if (c && typeof c === "object") out += textOf(c as Json);
  }
  return out;
}

describe("RN leaf pack renders the sample area", () => {
  const nodes = [...walk(render())];
  const byType = (t: string) => nodes.filter((n) => n.type === t);
  const allText = nodes.filter((n) => n.type === "Text").map(textOf);

  it("renders without hitting the Unsupported fallback", () => {
    expect(nodes.length).toBeGreaterThan(0);
    expect(allText.some((t) => t.includes("Unsupported"))).toBe(false);
  });

  it("renders Label/Markdown as <Text> (the header + intro)", () => {
    expect(allText).toContain("MeshWeaver on React Native");
    expect(allText.some((t) => t.includes("native leaves"))).toBe(true);
  });

  it("binds a TextField to /data/name (a <TextInput> carrying the resolved value)", () => {
    const inputs = byType("TextInput");
    expect(inputs).toHaveLength(1);
    expect(inputs[0].props.value).toBe("Ada Lovelace");
  });

  it("binds a CheckBox to /data/active (a <Switch> reflecting the boolean)", () => {
    const switches = byType("Switch");
    expect(switches).toHaveLength(1);
    expect(switches[0].props.value).toBe(true);
  });

  it("renders a Button (<Pressable>) with its label", () => {
    const pressables = byType("Pressable");
    expect(pressables.length).toBeGreaterThan(0);
    expect(allText).toContain("Save");
  });

  it("renders the Badge text", () => {
    expect(allText).toContain("Green");
  });

  it("renders the DataGrid: header titles + every bound row cell", () => {
    expect(allText).toContain("Account"); // column title
    expect(allText).toContain("Amount");
    expect(allText).toContain("ACME"); // /data/rows[0].name
    expect(allText).toContain("124000"); // /data/rows[0].amount
    expect(allText).toContain("Northwind"); // /data/rows[1].name
  });
});
