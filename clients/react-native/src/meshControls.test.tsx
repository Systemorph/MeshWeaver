import { describe, expect, it } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource, localize, type AreaTree } from "@meshweaver/react/core";

import { rnPack } from "./rnPack";

// Assert against the CATALOG, not a literal — that also pins that these strings are localized.
const en = (key: string) => localize(key, "en");

// Headless proof that the mesh display controls render (and the picker binds) — so a streamed portal
// area lands on real native components, never the Unsupported fallback.

const ref = (area: string) => ({ $type: "NamedArea", area });
const ptr = (pointer: string) => ({ $type: "JsonPointerReference", pointer });

const tree: AreaTree = {
  data: { picker: "acme/Sales" },
  areas: {
    main: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack" }],
      areas: [ref("picker"), ref("user"), ref("bubbleU"), ref("bubbleA"), ref("card"), ref("ladef"), ref("chat"), ref("search")],
    },
    picker: { $type: "MeshNodePicker", label: "Node", data: ptr("/data/picker") },
    user: { $type: "UserProfile", name: "Ada Lovelace" },
    bubbleU: { $type: "ThreadMessageBubble", role: "user", message: "hi there" },
    bubbleA: { $type: "ThreadMessageBubble", role: "assistant", message: "hello back" },
    card: { $type: "MeshNodeCard", nodePath: "acme/Sales", title: "Sales", description: "Q3 numbers", imageUrl: "📊" },
    ladef: { $type: "LayoutAreaDefinition", definition: { title: "Overview", description: "the overview area" } },
    chat: { $type: "ThreadChat" },
    search: { $type: "MeshSearch" },
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

describe("RN leaf pack renders the mesh display controls", () => {
  const nodes = [...walk(render())];
  const byType = (t: string) => nodes.filter((n) => n.type === t);
  const inputValues = byType("TextInput").map((n) => String(n.props.value ?? ""));
  const allText = byType("Text").map(textOf);
  const hasText = (needle: string) => allText.some((t) => t.includes(needle));

  it("never hits the Unsupported fallback", () => {
    expect(hasText("Unsupported")).toBe(false);
  });

  it("MeshNodePicker binds the node path", () => {
    expect(inputValues).toContain("acme/Sales");
  });

  it("UserProfile shows the avatar initial + name", () => {
    expect(hasText("Ada Lovelace")).toBe(true);
    expect(hasText("A")).toBe(true); // avatar initial
  });

  it("ThreadMessageBubble renders both roles' text", () => {
    expect(hasText("hi there")).toBe(true);
    expect(hasText("hello back")).toBe(true);
  });

  it("MeshNodeCard shows emoji, title, description", () => {
    expect(hasText("📊")).toBe(true);
    expect(hasText("Sales")).toBe(true);
    expect(hasText("Q3 numbers")).toBe(true);
  });

  it("LayoutAreaDefinition shows title + description", () => {
    expect(hasText("Overview")).toBe(true);
    expect(hasText("the overview area")).toBe(true);
  });

  // The live-ops controls used to be labelled placeholder badges ("▦ Thread chat"). They are real
  // implementations now, so with NO MeshOpsProvider they must render their own EMPTY state — the
  // same degradation the web pack has — rather than a badge or the Unsupported fallback.
  it("ThreadChat renders its real empty state and composer without a mesh", () => {
    expect(hasText("▦ Thread chat")).toBe(false);
    expect(hasText(en("chat.startConversation"))).toBe(true);
    expect(hasText(en("common.send"))).toBe(true);
  });

  it("MeshSearch renders its real search box without a mesh", () => {
    expect(hasText("▦ Search results")).toBe(false);
    const placeholders = nodes
      .filter((n) => n.type === "TextInput")
      .map((n) => String(n.props.placeholder ?? ""));
    expect(placeholders).toContain(en("common.typeToSearch"));
  });
});
