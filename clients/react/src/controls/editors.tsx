// Editor controls. CodeEditor / MarkdownEditor / DiffEditor render on real Monaco (controls/monaco.tsx
// — lazily loaded, with a bound-textarea fallback until it arrives). EditForm is normally a SKIN
// (EditFormControl is a container whose EditFormSkin renders in render/skins.tsx); the leaf entry
// here covers a skinless EditForm control by rendering its child property areas as a form.

import type { ReactNode } from "react";
import { Text } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import { RenderChildren } from "../render/ControlRenderer.js";
import { str } from "./common.js";
import { CollaborativeMarkdownView } from "./display.js";
import { CodeEditorView, DiffEditorView, MarkdownEditorView } from "./monaco.js";

function EditFormView({ control }: { control: UiControl }): ReactNode {
  if (Array.isArray(control.areas))
    return (
      <form onSubmit={(e) => e.preventDefault()} style={{ display: "flex", flexDirection: "column", gap: 12 }}>
        <RenderChildren control={control} />
      </form>
    );
  return (
    <Text italic size={200}>
      Edit form (auto-generated) — {str(control.$type)}
    </Text>
  );
}

export const editorControls = {
  CodeEditor: CodeEditorView,
  Editor: CodeEditorView,
  MarkdownEditor: MarkdownEditorView,
  // The read-only rendered overview of markdown nodes — a DISPLAY view (display.tsx), never the
  // editor: mapping it to MarkdownEditorView was the "markdown pages open in edit mode" parity bug.
  CollaborativeMarkdown: CollaborativeMarkdownView,
  DiffEditor: DiffEditorView,
  EditForm: EditFormView,
};
