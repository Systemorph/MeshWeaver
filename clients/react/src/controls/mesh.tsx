import type { ReactNode } from "react";
import { Avatar, Badge, Card, Input, Text } from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshLink } from "../area/navigation.js";
import { controlClass, mergeClass } from "../render/style.js";
import { useThemeMode } from "../theme/themeMode.js";
import { str, useField, useText } from "./common.js";
import { AddressAreaEmbed, sanitizeInlineSvg } from "./display.js";
import { ThreadChatView } from "./threadChat.js";
import { MeshNodeCollectionView, MeshSearchView } from "./meshLive.js";
import { documentControls } from "./documentControls.js";
import { nodeTransferControls } from "./nodeTransfer.js";
import { meshNodeEditorControls } from "./meshNodeEditor.js";
import { fileBrowserControls } from "./fileBrowser.js";

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
  const link = useMeshLink(str(def.url) || undefined);
  return (
    <a href={link.href} onClick={link.onClick} style={{ textDecoration: "none" }}>
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

/**
 * A mesh node rendered as a card — the React twin of Blazor's MeshNodeCardView
 * (src/MeshWeaver.Graph/MeshNodeCardControl.cs): image / inline-svg / emoji / initial placeholder +
 * truncated title + description, the whole card navigating to /{NodePath} unless DisableNavigation.
 * With ItemArea set the card DELEGATES its rendering to that layout area on the node's own hub.
 */
function MeshNodeCardView({ control }: { control: UiControl }): ReactNode {
  const nodePath = useText(control.nodePath);
  const title = useText(control.title) || nodePath;
  const description = useText(control.description);
  const imageUrl = useText(control.imageUrl);
  const itemArea = useText(control.itemArea);
  const disableNavigation = !!useResolve(control.disableNavigation);
  const link = useMeshLink(!disableNavigation && nodePath ? `/${nodePath}` : undefined);

  if (itemArea && nodePath) {
    const embed = <AddressAreaEmbed address={nodePath} area={itemArea} />;
    if (!link.href) return embed;
    return (
      <a href={link.href} onClick={link.onClick} style={{ textDecoration: "none", color: "inherit", display: "block" }}>
        {embed}
      </a>
    );
  }

  const isInlineSvg = imageUrl.trimStart().toLowerCase().startsWith("<svg");
  const isEmoji = !!imageUrl && !isInlineSvg && !imageUrl.includes("/") && !imageUrl.startsWith("data:");
  const truncatedTitle = title.length > 64 ? `${title.slice(0, 63).trimEnd()}…` : title;
  const truncatedDescription = description.length > 100 ? `${description.slice(0, 97)}...` : description;
  const placeholderStyle: React.CSSProperties = {
    width: 48,
    height: 48,
    flexShrink: 0,
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 6,
  };

  const card = (
    <Card className={mergeClass("mesh-node-card", controlClass(control))} style={{ cursor: link.href ? "pointer" : undefined, width: "100%", padding: 12 }}>
      <div className="mesh-node-card-content" style={{ display: "flex", alignItems: "center", gap: 12, minWidth: 0 }}>
        {isInlineSvg ? (
          <div className="mesh-node-card-image" style={placeholderStyle} dangerouslySetInnerHTML={{ __html: sanitizeInlineSvg(imageUrl) }} />
        ) : isEmoji ? (
          <div className="mesh-node-card-placeholder" style={{ ...placeholderStyle, fontSize: 32 }}>
            {imageUrl}
          </div>
        ) : imageUrl ? (
          <img src={imageUrl} alt={title} className="mesh-node-card-image" style={{ ...placeholderStyle, objectFit: "cover" }} />
        ) : (
          <div
            className="mesh-node-card-placeholder"
            style={{ ...placeholderStyle, background: "var(--colorBrandBackground2)", fontWeight: 600, fontSize: 20 }}
          >
            {(title[0] ?? "?").toUpperCase()}
          </div>
        )}
        <div className="mesh-node-card-text" style={{ display: "flex", flexDirection: "column", minWidth: 0 }}>
          <Text
            className="mesh-node-card-title"
            weight="semibold"
            title={title}
            style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}
          >
            {truncatedTitle}
          </Text>
          {description ? (
            <Text className="mesh-node-card-description" size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
              {truncatedDescription}
            </Text>
          ) : null}
        </div>
      </div>
    </Card>
  );

  if (!link.href) return card;
  return (
    <a href={link.href} onClick={link.onClick} style={{ textDecoration: "none", color: "inherit", display: "block", width: "100%" }}>
      {card}
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
 * The remaining registered-but-placeholder $types. parity.test.ts ratchets this list: it may only
 * SHRINK as controls get real implementations — it is now EMPTY (every mesh control is real).
 */
export const placeholderControlTypes = [] as const;

const placeholderLabels: Record<(typeof placeholderControlTypes)[number], string> = {};

export const meshControls = {
  MeshSearch: MeshSearchView,
  MeshNodeCollection: MeshNodeCollectionView,
  MeshNodeCard: MeshNodeCardView,
  // SearchBox renders via inputs.tsx's bound SearchBoxView.
  MeshNodePicker: MeshNodePickerView,
  ...meshNodeEditorControls,
  UserProfile: UserProfileView,
  ThreadMessageBubble: ThreadMessageBubbleView,
  ThreadChat: ThreadChatView,
  LayoutAreaDefinition: LayoutAreaDefinitionView,
  // Document, node-transfer, and file-browser controls (formerly placeholders) — real implementations.
  ...documentControls,
  ...nodeTransferControls,
  ...fileBrowserControls,
  ...Object.fromEntries(placeholderControlTypes.map((t) => [t, placeholder(placeholderLabels[t])])),
};
