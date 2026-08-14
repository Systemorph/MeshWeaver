// Issue #1476 — an auto-saving editor's edits must reach the node.
//
// `MarkdownEditorControl.AutoSaveAddress` / `CodeEditorControl.AutoSaveAddress` name a mesh node the
// editor edits IN PLACE. Such a control carries a literal value and NO binding pointer, so the
// renderer's ordinary area-pointer `update` event has nothing to write to: the JS packs emitted it
// anyway, the edit went nowhere, and nothing failed visibly — the text simply was not there after a
// reload. These tests pin the write path and the patch shape.

import { beforeAll, afterEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl } from "../core.js";
import type { MeshNodeState, MeshOps, ThreadSubmitOptions } from "../live/meshOps.js";
import { AUTO_SAVE_DEBOUNCE_MS, autoSaveContentPatch } from "./autoSave.js";

// Monaco cannot run in jsdom; the control's own bound-textarea fallback renders instead, which is
// the SAME binding — exactly what a non-DOM host (and React Native) uses.
vi.mock("@monaco-editor/react", () => ({ default: null, Editor: null, DiffEditor: null }));

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

afterEach(() => vi.useRealTimers());

const NODE = "rbuergi/Notes/today";

class FakeOps implements MeshOps {
  readonly patchCalls: { path: string; fields: Record<string, unknown> }[] = [];
  async *watch(path: string): AsyncIterableIterator<MeshNodeState> {
    yield { path, content: {} };
    await new Promise<void>(() => undefined);
  }
  async startThread(ns: string, _t: string, _o?: ThreadSubmitOptions) {
    return { path: `${ns}/_Thread/x` };
  }
  async submitMessage(_p: string, _t: string, _o?: ThreadSubmitOptions) {
    return "m";
  }
  patch(path: string, fields: Record<string, unknown>): void {
    this.patchCalls.push({ path, fields });
  }
}

function tree(control: Record<string, unknown>): AreaTree {
  return { data: {}, areas: { main: control as unknown as UiControl } };
}

function renderEditor(control: Record<string, unknown>, ops: MeshOps | null) {
  return render(<MeshAreaView source={new StaticAreaSource(tree(control))} rootArea="main" ops={ops ?? undefined} />);
}

function type(text: string) {
  fireEvent.change(screen.getByRole("textbox"), { target: { value: text } });
}

describe("autoSaveContentPatch — the merge patch that persists the edit", () => {
  it("writes MarkdownContent.Content and clears the caches derived from the OLD text", () => {
    // prerenderedHtml is preferred by readers (ContentLayoutArea), so leaving it would render the
    // text the user just replaced; codeSubmissions is extracted from the same stale text.
    expect(autoSaveContentPatch("markdown", "# new")).toEqual({
      content: { content: "# new", prerenderedHtml: null, codeSubmissions: null },
    });
  });

  it("writes CodeConfiguration.Code and touches nothing else", () => {
    // A merge patch leaves language / isExecutable / the polymorphic $type intact — the same
    // "preserve every other field" contract CodeEditorView.razor's AutoSaveCode implements.
    expect(autoSaveContentPatch("code", "var x = 1;")).toEqual({ content: { code: "var x = 1;" } });
  });
});

describe("MarkdownEditor with autoSaveAddress", () => {
  it("patches the named node after the typing pause", () => {
    vi.useFakeTimers();
    const ops = new FakeOps();
    renderEditor({ $type: "MarkdownEditor", value: "# old", autoSaveAddress: NODE }, ops);

    type("# edited");
    expect(ops.patchCalls, "must not write on every keystroke").toHaveLength(0);

    act(() => void vi.advanceTimersByTime(AUTO_SAVE_DEBOUNCE_MS));
    expect(ops.patchCalls).toEqual([
      { path: NODE, fields: { content: { content: "# edited", prerenderedHtml: null, codeSubmissions: null } } },
    ]);
  });

  it("coalesces a burst of keystrokes into ONE write of the final text", () => {
    vi.useFakeTimers();
    const ops = new FakeOps();
    renderEditor({ $type: "MarkdownEditor", value: "", autoSaveAddress: NODE }, ops);

    type("a");
    act(() => void vi.advanceTimersByTime(100));
    type("ab");
    act(() => void vi.advanceTimersByTime(100));
    type("abc");
    act(() => void vi.advanceTimersByTime(AUTO_SAVE_DEBOUNCE_MS));

    expect(ops.patchCalls).toHaveLength(1);
    expect((ops.patchCalls[0].fields.content as Record<string, unknown>).content).toBe("abc");
  });

  it("FLUSHES a pending edit when the editor unmounts — navigating away mid-pause must not lose it", () => {
    vi.useFakeTimers();
    const ops = new FakeOps();
    const view = renderEditor({ $type: "MarkdownEditor", value: "", autoSaveAddress: NODE }, ops);

    type("half a sentence");
    act(() => view.unmount());

    expect(ops.patchCalls).toHaveLength(1);
    expect((ops.patchCalls[0].fields.content as Record<string, unknown>).content).toBe("half a sentence");
  });
});

describe("CodeEditor with autoSaveAddress", () => {
  it("patches CodeConfiguration.Code", () => {
    vi.useFakeTimers();
    const ops = new FakeOps();
    renderEditor({ $type: "CodeEditor", value: "old();", autoSaveAddress: NODE, language: "csharp" }, ops);

    type("neu();");
    act(() => void vi.advanceTimersByTime(AUTO_SAVE_DEBOUNCE_MS));
    expect(ops.patchCalls).toEqual([{ path: NODE, fields: { content: { code: "neu();" } } }]);
  });
});

describe("an editor that did NOT opt in is untouched", () => {
  it("writes nothing when there is no autoSaveAddress", () => {
    vi.useFakeTimers();
    const ops = new FakeOps();
    renderEditor({ $type: "CodeEditor", value: "x" }, ops);

    type("y");
    act(() => void vi.advanceTimersByTime(AUTO_SAVE_DEBOUNCE_MS * 4));
    expect(ops.patchCalls).toEqual([]);
  });

  it("does not crash a host with no mesh ops", () => {
    vi.useFakeTimers();
    renderEditor({ $type: "MarkdownEditor", value: "x", autoSaveAddress: NODE }, null);
    type("y");
    act(() => void vi.advanceTimersByTime(AUTO_SAVE_DEBOUNCE_MS * 4));
    expect(screen.getByRole("textbox")).toBeTruthy();
  });
});
