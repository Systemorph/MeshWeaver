// Monaco editor parity — CodeEditor / MarkdownEditor / DiffEditor render REAL Monaco
// (@monaco-editor/react) with the Blazor Monaco views' prop mapping
// (Monaco/CodeEditorView.razor: value/language/height/readonly defaults; Monaco/DiffEditorView.razor:
// originalContent/modifiedContent/language/height, read-only side-by-side).
// The module is mocked here (jsdom can't host the real editor), which also pins the exact props the
// control hands Monaco — the wire-to-editor contract.

import { beforeAll, describe, expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl } from "../core.js";
import { monacoSettings } from "./monaco.js";

interface CapturedProps {
  language?: string;
  height?: string;
  theme?: string;
  value?: string;
  options?: Record<string, unknown>;
  original?: string;
  modified?: string;
  onChange?: (v: string | undefined) => void;
}

vi.mock("@monaco-editor/react", () => {
  const editorCalls: CapturedProps[] = [];
  const diffCalls: CapturedProps[] = [];
  const Editor = (props: CapturedProps) => {
    editorCalls.push(props);
    return <div data-testid="monaco-editor" data-language={props.language} data-theme={props.theme} data-readonly={String(props.options?.readOnly)} data-height={props.height} />;
  };
  const DiffEditor = (props: CapturedProps) => {
    diffCalls.push(props);
    return <div data-testid="monaco-diff" data-language={props.language} data-original={props.original} data-modified={props.modified} />;
  };
  return { default: Editor, Editor, DiffEditor, __editorCalls: editorCalls, __diffCalls: diffCalls };
});

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

async function mockCalls(): Promise<{ editor: CapturedProps[]; diff: CapturedProps[] }> {
  const mod = (await import("@monaco-editor/react")) as unknown as { __editorCalls: CapturedProps[]; __diffCalls: CapturedProps[] };
  return { editor: mod.__editorCalls, diff: mod.__diffCalls };
}

function tree(control: Record<string, unknown>, data: Record<string, unknown> = {}): AreaTree {
  return { data, areas: { main: control as unknown as UiControl } };
}

describe("monacoSettings — the CodeEditorControl → Monaco option mapping", () => {
  it("applies the Blazor defaults (plaintext, 300px, line numbers on, not read-only)", () => {
    const s = monacoSettings({}, false);
    expect(s.language).toBe("plaintext");
    expect(s.height).toBe("300px");
    expect(s.theme).toBe("light");
    expect(s.options).toMatchObject({ readOnly: false, lineNumbers: "on", minimap: { enabled: false }, wordWrap: "off" });
  });

  it("maps the wire props (language, readonly, height, lineNumbers, minimap, wordWrap, theme)", () => {
    const s = monacoSettings(
      { language: "csharp", readonly: true, height: "500px", lineNumbers: false, minimap: true, wordWrap: true, theme: "hc-black" },
      true,
    );
    expect(s).toMatchObject({
      language: "csharp",
      height: "500px",
      theme: "hc-black", // explicit theme wins over dark-mode default
      options: { readOnly: true, lineNumbers: "off", minimap: { enabled: true }, wordWrap: "on" },
    });
    expect(monacoSettings({}, true).theme).toBe("vs-dark"); // dark mode default
  });
});

describe("CodeEditorView — real Monaco with the bound value", () => {
  it("mounts Monaco with language/height/readOnly from the control and the resolved value", async () => {
    const source = new StaticAreaSource(
      tree(
        { $type: "CodeEditor", value: { $type: "JsonPointerReference", pointer: "/data/code" }, language: "csharp", height: "220px", readonly: true },
        { code: "var x = 1;" },
      ),
    );
    render(<MeshAreaView source={source} rootArea="main" />);
    const el = await screen.findByTestId("monaco-editor");
    expect(el.getAttribute("data-language")).toBe("csharp");
    expect(el.getAttribute("data-height")).toBe("220px");
    expect(el.getAttribute("data-readonly")).toBe("true");
    const { editor } = await mockCalls();
    expect(editor[editor.length - 1].value).toBe("var x = 1;");
  });

  it("edits write back through the standard update event to the value pointer", async () => {
    const source = new StaticAreaSource(
      tree({ $type: "CodeEditor", value: { $type: "JsonPointerReference", pointer: "/data/code" } }, { code: "before" }),
    );
    render(<MeshAreaView source={source} rootArea="main" />);
    await screen.findByTestId("monaco-editor");
    const { editor } = await mockCalls();
    act(() => editor[editor.length - 1].onChange?.("after"));
    const update = source.events.find((e) => e.kind === "update");
    expect(update).toMatchObject({ kind: "update", area: "main", pointer: "/data/code", value: "after" });
    expect(source.getState().data?.code).toBe("after");
  });

  it("MarkdownEditor forces markdown language + word wrap", async () => {
    const source = new StaticAreaSource(tree({ $type: "MarkdownEditor", value: "# hi" }));
    render(<MeshAreaView source={source} rootArea="main" />);
    const el = await screen.findByTestId("monaco-editor");
    expect(el.getAttribute("data-language")).toBe("markdown");
    const { editor } = await mockCalls();
    expect(editor[editor.length - 1].options?.wordWrap).toBe("on");
  });
});

describe("DiffEditorView — read-only side-by-side diff", () => {
  it("mounts the Monaco diff with originalContent/modifiedContent/language and pane labels", async () => {
    const source = new StaticAreaSource(
      tree({
        $type: "DiffEditor",
        originalContent: "old text",
        modifiedContent: "new text",
        originalLabel: "Version 3",
        modifiedLabel: "Current",
        language: "json",
        height: "400px",
      }),
    );
    render(<MeshAreaView source={source} rootArea="main" />);
    const el = await screen.findByTestId("monaco-diff");
    expect(el.getAttribute("data-original")).toBe("old text");
    expect(el.getAttribute("data-modified")).toBe("new text");
    expect(el.getAttribute("data-language")).toBe("json");
    expect(screen.getByText("Version 3")).toBeTruthy();
    expect(screen.getByText("Current")).toBeTruthy();
    const { diff } = await mockCalls();
    expect(diff[diff.length - 1].options).toMatchObject({ readOnly: true, renderSideBySide: true, originalEditable: false });
  });

  it("defaults language markdown / height 500px / Original+Current labels", async () => {
    const source = new StaticAreaSource(tree({ $type: "DiffEditor", originalContent: "a", modifiedContent: "b" }));
    render(<MeshAreaView source={source} rootArea="main" />);
    const el = await screen.findByTestId("monaco-diff");
    expect(el.getAttribute("data-language")).toBe("markdown");
    expect(screen.getByText("Original")).toBeTruthy();
    expect(screen.getByText("Current")).toBeTruthy();
    const { diff } = await mockCalls();
    expect(diff[diff.length - 1].height).toBe("500px");
  });
});
