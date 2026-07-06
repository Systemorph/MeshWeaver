import { useState, type ReactNode } from "react";
import { Checkbox, Dropdown, Field, Input, Option, Text } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import { useMeshOps } from "../live/meshOps.js";
import { useNodeState } from "../live/nodeState.js";

/** One backend-computed editable field (the MeshNodeEditorField wire shape, camelCase). */
interface EditorField {
  key: string;
  label: string;
  kind: "Text" | "Bool" | "Enum" | string;
  options?: string[];
}

/**
 * A single text/enum-less field with its own draft state: edits stay local while typing and commit
 * on blur, so we post ONE field patch per edit (not one per keystroke) and never fight the caret when
 * a remote update lands mid-typing. After blur the draft clears and the field re-reflects node state.
 */
function TextFieldRow({
  label,
  value,
  canEdit,
  onCommit,
}: {
  label: string;
  value: string;
  canEdit: boolean;
  onCommit: (v: string) => void;
}): ReactNode {
  const [draft, setDraft] = useState<string | null>(null);
  const shown = draft ?? value;
  return (
    <Field label={label}>
      <Input
        value={shown}
        readOnly={!canEdit}
        onChange={(_, d) => setDraft(d.value)}
        onBlur={() => {
          if (draft != null && draft !== value) onCommit(draft);
          setDraft(null);
        }}
      />
    </Field>
  );
}

/**
 * React twin of Blazor's MeshNodeContentEditorView
 * (src/MeshWeaver.Graph/MeshNodeContentEditorControl.cs): a data-bound form for a mesh node's
 * scalar / bool / enum content fields. Reads the node's live content via MeshOps.watch(nodePath) and
 * writes each field back with a field-level RFC 7396 patch — ops.patch(nodePath, { content: { key: v } }),
 * the client twin of GetMeshNodeStream(nodePath).Update(...). The fields (key/label/kind/options) are
 * computed on the backend and carried on the control, so no client-side type registry is needed. ONE
 * source of truth (the node stream) — no /data replica, no debounced save loop.
 */
function MeshNodeContentEditorView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const nodePath = typeof control.nodePath === "string" ? control.nodePath : "";
  const canEdit = control.canEdit !== false;
  const fields = (Array.isArray(control.fields) ? control.fields : []) as EditorField[];
  const node = useNodeState(ops, nodePath || null);
  const content = (node?.content ?? {}) as Record<string, unknown>;

  if (!nodePath) return <Text italic>No node bound.</Text>;

  const write = (key: string, value: unknown) => {
    if (ops && canEdit) ops.patch(nodePath, { content: { [key]: value } });
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12, width: "100%" }}>
      {fields.map((fld) => {
        const value = content[fld.key];
        if (fld.kind === "Bool") {
          return (
            <Checkbox
              key={fld.key}
              label={fld.label}
              checked={value === true}
              disabled={!canEdit}
              onChange={(_, d) => write(fld.key, d.checked === true)}
            />
          );
        }
        if (fld.kind === "Enum") {
          const current = value == null ? "" : String(value);
          return (
            <Field key={fld.key} label={fld.label}>
              <Dropdown
                value={current}
                selectedOptions={current ? [current] : []}
                disabled={!canEdit}
                onOptionSelect={(_, d) => {
                  if (d.optionValue != null) write(fld.key, d.optionValue);
                }}
              >
                {(fld.options ?? []).map((opt) => (
                  <Option key={opt} value={opt}>
                    {opt}
                  </Option>
                ))}
              </Dropdown>
            </Field>
          );
        }
        return (
          <TextFieldRow
            key={fld.key}
            label={fld.label}
            value={value == null ? "" : String(value)}
            canEdit={canEdit}
            onCommit={(v) => write(fld.key, v)}
          />
        );
      })}
    </div>
  );
}

/** The mesh-node content-editor control ($type → component), spread into the mesh pack. */
export const meshNodeEditorControls = {
  MeshNodeContentEditor: MeshNodeContentEditorView,
};
