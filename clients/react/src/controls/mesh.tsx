import type { ReactNode } from "react";
import { Avatar, Badge, Card, Input, Text } from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { useThemeMode } from "../theme/themeMode.js";
import { str, useField, useText } from "./common.js";
import { ThreadChatView } from "./threadChat.js";
import { MeshNodeCollectionView, MeshSearchView } from "./meshLive.js";

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

/**
 * Catalog card for a layout-area definition — the mirror of Blazor's LayoutAreaDefinitionView:
 * a link card with title, description, and a thumbnail picked light/dark-aware
 * (definition.thumbnailUrl → theme thumbnail → definition.imageUrl), cache-busted by thumbnailHash.
 */
function LayoutAreaDefinitionView({ control }: { control: UiControl }): ReactNode {
  const { resolved } = useThemeMode();
  const def = (control.definition ?? {}) as Json;
  const hash = str(control.thumbnailHash);
  const themed = resolved === "dark" ? control.darkThumbnailUrl : control.lightThumbnailUrl;
  const raw = str(def.thumbnailUrl) || str(themed) || str(def.imageUrl);
  const img = raw && hash ? `${raw}${raw.includes("?") ? "&" : "?"}v=${hash}` : raw;
  const title = str(def.title ?? def.area);
  return (
    <a href={str(def.url) || undefined} style={{ textDecoration: "none" }}>
      <Card style={{ width: 260, padding: 12, gap: 8 }}>
        <Text weight="semibold">{title}</Text>
        <div style={{ display: "flex", gap: 8, alignItems: "flex-start" }}>
          {img ? <img src={img} alt={`Thumbnail for ${title}`} width={80} height={80} style={{ objectFit: "cover", borderRadius: 4 }} /> : null}
          <Text size={200} title={str(def.description)}>
            {str(def.description)}
          </Text>
        </div>
      </Card>
    </a>
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

/**
 * The remaining registered-but-placeholder $types — every entry needs a live mesh service
 * (file storage, thread execution, import/export pipelines) beyond the AreaSource contract.
 * parity.test.ts ratchets this list: it may only SHRINK as controls get real implementations.
 */
export const placeholderControlTypes = [
  "FileBrowser",
  "ExportDocument",
  "NodeExport",
  "NodeImport",
  "DocumentSource",
] as const;

const placeholderLabels: Record<(typeof placeholderControlTypes)[number], string> = {
  FileBrowser: "File browser",
  ExportDocument: "Export document",
  NodeExport: "Node export",
  NodeImport: "Node import",
  DocumentSource: "Document source",
};

export const meshControls = {
  MeshSearch: MeshSearchView,
  MeshNodeCollection: MeshNodeCollectionView,
  // SearchBox renders via inputs.tsx's bound SearchBoxView.
  MeshNodePicker: MeshNodePickerView,
  UserProfile: UserProfileView,
  ThreadMessageBubble: ThreadMessageBubbleView,
  ThreadChat: ThreadChatView,
  LayoutAreaDefinition: LayoutAreaDefinitionView,
  ...Object.fromEntries(placeholderControlTypes.map((t) => [t, placeholder(placeholderLabels[t])])),
};
