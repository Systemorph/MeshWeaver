import type { ReactNode } from "react";
import { Avatar, Badge, Input, Text } from "@fluentui/react-components";
import { Search20Regular } from "@fluentui/react-icons";
import type { UiControl } from "../area/types.js";
import { str, useField, useText } from "./common.js";

function MeshSearchView(): ReactNode {
  return <Input contentBefore={<Search20Regular />} placeholder="Search the mesh…" />;
}

function MeshNodePickerView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  return <Input value={str(f.value)} placeholder="Pick a node (path)…" onChange={(_, d) => f.setValue(d.value)} onBlur={f.onBlur} />;
}

function UserProfileView({ control }: { control: UiControl }): ReactNode {
  const name = useText(control.name) || useText(control.data);
  return (
    <span style={{ display: "inline-flex", alignItems: "center", gap: 8 }}>
      <Avatar name={name || undefined} size={28} />
      <Text>{name}</Text>
    </span>
  );
}

function ThreadMessageBubbleView({ control }: { control: UiControl }): ReactNode {
  const role = useText(control.role) || "user";
  const mine = /user/i.test(role);
  return (
    <div style={{ display: "flex", justifyContent: mine ? "flex-end" : "flex-start" }}>
      <div
        style={{
          maxWidth: "75%",
          padding: "8px 12px",
          borderRadius: 12,
          background: mine ? "var(--colorBrandBackground2)" : "var(--colorNeutralBackground3)",
        }}
      >
        <Text size={200}>{useText(control.message) || useText(control.data)}</Text>
      </div>
    </div>
  );
}

/** A clean labeled placeholder for the long-tail of specialized controls (full impls are follow-ups). */
function placeholder(label: string) {
  return function Placeholder({ control }: { control: UiControl }): ReactNode {
    return (
      <Badge appearance="outline" color="informative">
        {label}
        {control.data != null ? `: ${str(control.data)}` : ""}
      </Badge>
    );
  };
}

export const meshControls = {
  MeshSearch: MeshSearchView,
  SearchBox: MeshSearchView,
  MeshNodePicker: MeshNodePickerView,
  UserProfile: UserProfileView,
  ThreadMessageBubble: ThreadMessageBubbleView,
  FileBrowser: placeholder("File browser"),
  ThreadChat: placeholder("Thread chat"),
  ExportDocument: placeholder("Export document"),
  NodeExport: placeholder("Node export"),
  NodeImport: placeholder("Node import"),
  DocumentSource: placeholder("Document source"),
  ItemTemplate: placeholder("Item template"),
  LayoutAreaDefinition: placeholder("Layout-area definition"),
  Appearance: placeholder("Appearance"),
};
