import { afterEach, describe, expect, it, vi } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import {
  AUTO_SAVE_DEBOUNCE_MS,
  MeshOpsProvider,
  RegistryProvider,
  RenderArea,
  ScopeProvider,
  StaticAreaSource,
  type AreaTree,
  type MeshOps,
  type UiControl,
} from "@meshweaver/react/core";
import { rnPack } from "./rnPack";

// Issue #1476 — the native editors must persist an auto-saving control's edits too.
//
// `MarkdownEditor` and `CodeEditor` used to be THE SAME component in this pack, bound only to a
// layout-area pointer. A control carrying `autoSaveAddress` has no pointer (it edits a mesh node in
// place), so an edit went nowhere and nothing failed visibly. They now differ by which content field
// they write, through the same `useAutoSave` the web pack uses.

type Json = ReactTestRendererJSON;

const NODE = "rbuergi/Notes/today";

function fakeOps(patchCalls: { path: string; fields: Record<string, unknown> }[]): MeshOps {
  return {
    watch: () => ({ async *[Symbol.asyncIterator]() { await new Promise(() => {}); } }),
    startThread: async () => ({ path: "p/_Thread/t1" }),
    submitMessage: async () => "m1",
    patch: (path: string, fields: Record<string, unknown>) => patchCalls.push({ path, fields }),
  } as MeshOps;
}

function tree(control: Record<string, unknown>): AreaTree {
  return { data: {}, areas: { main: control as unknown as UiControl } };
}

function renderAndType(control: Record<string, unknown>, ops: MeshOps, text: string) {
  let root!: TestRenderer.ReactTestRenderer;
  TestRenderer.act(() => {
    root = TestRenderer.create(
      <RegistryProvider pack={rnPack}>
        <MeshOpsProvider ops={ops}>
          <ScopeProvider source={new StaticAreaSource(tree(control))} area="main">
            <RenderArea areaKey="main" />
          </ScopeProvider>
        </MeshOpsProvider>
      </RegistryProvider>,
    );
  });
  const input = find(root.toJSON() as Json, "TextInput");
  TestRenderer.act(() => input!.props.onChangeText(text));
  return root;
}

function find(node: Json | Json[] | null, type: string): Json | null {
  if (node == null) return null;
  if (Array.isArray(node)) {
    for (const n of node) {
      const hit = find(n, type);
      if (hit) return hit;
    }
    return null;
  }
  if (node.type === type) return node;
  for (const c of node.children ?? []) {
    const hit = find(c as Json, type);
    if (hit) return hit;
  }
  return null;
}

afterEach(() => vi.useRealTimers());

describe("RN editors honour autoSaveAddress", () => {
  it("MarkdownEditor patches MarkdownContent.Content after the typing pause", () => {
    vi.useFakeTimers();
    const calls: { path: string; fields: Record<string, unknown> }[] = [];
    renderAndType(
      { $type: "MarkdownEditor", label: "Notes", data: "# old", autoSaveAddress: NODE },
      fakeOps(calls),
      "# edited",
    );
    expect(calls, "must not write on every keystroke").toHaveLength(0);
    TestRenderer.act(() => void vi.advanceTimersByTime(AUTO_SAVE_DEBOUNCE_MS));
    expect(calls).toEqual([
      { path: NODE, fields: { content: { content: "# edited", prerenderedHtml: null, codeSubmissions: null } } },
    ]);
  });

  it("CodeEditor patches CodeConfiguration.Code — the two are no longer the same component", () => {
    vi.useFakeTimers();
    const calls: { path: string; fields: Record<string, unknown> }[] = [];
    renderAndType(
      { $type: "CodeEditor", label: "Code", data: "old();", autoSaveAddress: NODE },
      fakeOps(calls),
      "neu();",
    );
    TestRenderer.act(() => void vi.advanceTimersByTime(AUTO_SAVE_DEBOUNCE_MS));
    expect(calls).toEqual([{ path: NODE, fields: { content: { code: "neu();" } } }]);
  });

  it("an editor without autoSaveAddress writes nothing", () => {
    vi.useFakeTimers();
    const calls: { path: string; fields: Record<string, unknown> }[] = [];
    renderAndType({ $type: "CodeEditor", label: "Code", data: "x" }, fakeOps(calls), "y");
    TestRenderer.act(() => void vi.advanceTimersByTime(AUTO_SAVE_DEBOUNCE_MS * 4));
    expect(calls).toEqual([]);
  });
});
