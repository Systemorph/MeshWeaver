import type { ReactNode } from "react";
import { Textarea, Text } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import { str, useField } from "./common.js";

// Textarea-based editors. Swap in Monaco (@monaco-editor/react) here for full parity with the Blazor
// BlazorMonaco editors — the binding (useField) stays the same.
function CodeEditorView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  return (
    <Textarea
      value={str(f.value)}
      disabled={f.disabled}
      onChange={(_, d) => f.setValue(d.value)}
      onBlur={f.onBlur}
      textarea={{ style: { fontFamily: "var(--fontFamilyMonospace)", minHeight: 180 } }}
      style={{ width: "100%" }}
    />
  );
}

function MarkdownEditorView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  return <Textarea value={str(f.value)} disabled={f.disabled} onChange={(_, d) => f.setValue(d.value)} onBlur={f.onBlur} style={{ width: "100%", minHeight: 160 }} />;
}

function DiffEditorView({ control }: { control: UiControl }): ReactNode {
  const original = str(control.original ?? control.originalData);
  const modified = str(control.modified ?? control.data);
  return (
    <div style={{ display: "flex", gap: 8 }}>
      <pre style={paneStyle("removed")}>{original}</pre>
      <pre style={paneStyle("added")}>{modified}</pre>
    </div>
  );
}

function paneStyle(kind: "added" | "removed"): React.CSSProperties {
  return {
    flex: 1,
    margin: 0,
    padding: 8,
    overflow: "auto",
    fontFamily: "var(--fontFamilyMonospace)",
    fontSize: 12,
    background: kind === "added" ? "rgba(16,124,16,0.08)" : "rgba(216,59,1,0.08)",
    borderRadius: 4,
  };
}

function EditFormView({ control }: { control: UiControl }): ReactNode {
  return <Text italic size={200}>Edit form (auto-generated) — {str(control.$type)}</Text>;
}

export const editorControls = {
  CodeEditor: CodeEditorView,
  Editor: CodeEditorView,
  MarkdownEditor: MarkdownEditorView,
  CollaborativeMarkdown: MarkdownEditorView,
  DiffEditor: DiffEditorView,
  EditForm: EditFormView,
};
