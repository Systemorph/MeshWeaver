// MeshNodeContentEditor parity with Blazor's MeshNodeContentEditorView: a data-bound form for a mesh
// node's scalar/bool/enum content. It reads the node's live content via MeshOps.watch(nodePath) and
// writes each field back with a field-level RFC 7396 patch (ops.patch(nodePath, { content: { key: v } })
// — the client twin of GetMeshNodeStream(nodePath).Update(...)). The fields are backend-computed and
// carried on the control, so the view needs no client-side type registry.

import { beforeAll, describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";
import type { MeshNodeState, MeshOps, ThreadSubmitOptions } from "../live/meshOps.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
  if (!(globalThis as any).ResizeObserver)
    (globalThis as any).ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
});

const NODE = "conflict/_Sync/partner";

// A MeshOps fake: watch(path) yields the node's content once then stays open; patch records calls.
class FakeOps implements MeshOps {
  readonly patchCalls: { path: string; fields: Record<string, unknown> }[] = [];
  constructor(private readonly content: Record<string, unknown>) {}

  async *watch(path: string): AsyncIterableIterator<MeshNodeState> {
    yield { path, name: path.split("/").pop(), content: this.content };
    await new Promise<void>(() => undefined);
  }
  async startThread(ns: string, _text: string, _opts?: ThreadSubmitOptions) {
    return { path: `${ns}/_Thread/x` };
  }
  async submitMessage(_path: string, _text: string, _opts?: ThreadSubmitOptions) {
    return "m";
  }
  patch(path: string, fields: Record<string, unknown>): void {
    this.patchCalls.push({ path, fields });
  }
}

function editorTree(control: Record<string, unknown>): AreaTree {
  return { data: {}, areas: { main: { $type: "MeshNodeContentEditor", nodePath: NODE, ...control } } };
}

const FIELDS = [
  { key: "remoteUrl", label: "Remote URL", kind: "Text" },
  { key: "active", label: "Active", kind: "Bool" },
  { key: "direction", label: "Direction", kind: "Enum", options: ["Bidirectional", "PushOnly", "PullOnly"] },
];

function renderEditor(ops: FakeOps, canEdit = true) {
  return render(
    <MeshAreaView
      source={new StaticAreaSource(editorTree({ canEdit, fields: FIELDS }))}
      rootArea="main"
      ops={ops}
    />,
  );
}

describe("MeshNodeContentEditor", () => {
  it("renders each field from the node's live content", async () => {
    const ops = new FakeOps({ remoteUrl: "https://remote.example", active: true, direction: "Bidirectional" });
    renderEditor(ops);
    expect(await screen.findByDisplayValue("https://remote.example")).toBeTruthy();
    expect(screen.getByLabelText("Active")).toBeTruthy();
  });

  it("writes a bool field as a field-level content patch", async () => {
    const ops = new FakeOps({ remoteUrl: "u", active: true, direction: "Bidirectional" });
    renderEditor(ops);
    const cb = await screen.findByLabelText("Active");
    fireEvent.click(cb); // true → false
    expect(ops.patchCalls).toContainEqual({ path: NODE, fields: { content: { active: false } } });
  });

  it("commits a text field on blur (one patch, not per keystroke)", async () => {
    const ops = new FakeOps({ remoteUrl: "old", active: false, direction: "PushOnly" });
    renderEditor(ops);
    const input = (await screen.findByDisplayValue("old")) as HTMLInputElement;
    fireEvent.change(input, { target: { value: "https://new.example" } });
    expect(ops.patchCalls).toHaveLength(0); // nothing yet — only on blur
    fireEvent.blur(input);
    expect(ops.patchCalls).toContainEqual({ path: NODE, fields: { content: { remoteUrl: "https://new.example" } } });
  });

  it("does not write when canEdit is false", async () => {
    const ops = new FakeOps({ remoteUrl: "u", active: true, direction: "Bidirectional" });
    renderEditor(ops, false);
    const cb = await screen.findByLabelText("Active");
    fireEvent.click(cb);
    expect(ops.patchCalls).toHaveLength(0);
  });
});
