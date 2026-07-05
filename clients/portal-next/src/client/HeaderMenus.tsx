"use client";

// The header's Node / Mesh / AI menus + the Settings gear — the React port of the Blazor shell's
// PortalLayoutBase header (src/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor):
//
//   - Menus are MESH-DRIVEN, never hardcoded: the node hub's RenderMenus renderer writes a
//     permission-filtered MenuControl into the $Menu:{Node|Mesh|AI} slots of the SAME layout-area
//     stream the page renders — this component reads those slots off the current page's live
//     AreaSource (the React twin of MenuStreamExtensions.GetMenu / IMenuItemsProvider).
//   - Hierarchical items flatten inline with separators (FlattenMenuItems); the current node's
//     name headlines the Node and Mesh menus.
//   - Item click: Href wins; the AI menu's "ai-new-thread" sentinel opens the chat side panel
//     fresh; otherwise navigate /{currentPath}/{area}.
//   - Settings gear: per-node /{path}/Settings; at the root, /GlobalSettings for platform admins
//     (probed by Admin-partition readability — the RLS-gated twin of hub.IsGlobalAdmin), else the
//     user's own page.

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import {
  Button,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
} from "@fluentui/react-components";
import {
  BranchFork20Regular,
  Cube20Regular,
  Grid20Regular,
  Settings20Regular,
  Sparkle20Regular,
} from "@fluentui/react-icons";
import type { AreaSource, Json, UiControl } from "@meshweaver/react";
import { useSyncExternalStore } from "react";
import { useLiveConnection, useNavigationState } from "./LiveConnection";
import { useSidePanel } from "./SidePanel";
import { MenuItemIcon } from "./icons";

/** NodeMenuItemDefinition off the wire (camelCase). */
export interface MenuItemDef {
  label: string;
  area: string;
  icon?: string;
  href?: string;
  tooltip?: string;
  order?: number;
  children?: MenuItemDef[];
}

const SEPARATOR: MenuItemDef = { label: "", area: "_separator" };

/** The sentinel area of the AI menu's "New thread" item (PortalLayoutBase.AiNewThreadAction). */
export const AI_NEW_THREAD_ACTION = "ai-new-thread";

function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}

function toMenuItem(raw: Json): MenuItemDef | null {
  if (raw == null || typeof raw !== "object" || Array.isArray(raw)) return null;
  const o = raw as Record<string, Json>;
  const label = str(o.label);
  const area = str(o.area);
  if (!label && !area) return null;
  const children = Array.isArray(o.children)
    ? (o.children.map(toMenuItem).filter(Boolean) as MenuItemDef[])
    : undefined;
  return {
    label,
    area,
    icon: str(o.icon) || undefined,
    href: str(o.href) || undefined,
    tooltip: str(o.tooltip) || undefined,
    order: typeof o.order === "number" ? o.order : 0,
    children: children && children.length > 0 ? children : undefined,
  };
}

/** PortalLayoutBase.FlattenMenuItems — parents with children inline as separator + children. */
export function flattenMenuItems(items: MenuItemDef[]): MenuItemDef[] {
  if (!items.some((i) => i.children && i.children.length > 0)) return items;
  const result: MenuItemDef[] = [];
  for (const item of items) {
    if (item.children && item.children.length > 0) {
      if (result.length > 0) result.push(SEPARATOR);
      result.push(...item.children);
    } else {
      result.push(item);
    }
  }
  return result;
}

/** Read the $Menu:{context} MenuControl items off the current page's live area tree. */
function useMenuItems(source: AreaSource | null, context: string): MenuItemDef[] {
  const tree = useSyncExternalStore(
    source ? source.subscribe : () => () => {},
    () => (source ? source.getState() : null),
    () => null,
  );
  const control = tree?.areas?.[`$Menu:${context}`] as UiControl | undefined;
  const items = control && Array.isArray(control.items) ? control.items : [];
  const mapped = items.map(toMenuItem).filter((i): i is MenuItemDef => i != null);
  mapped.sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
  return mapped;
}

function MenuButton({
  context,
  icon,
  title,
  items,
  header,
  onItem,
}: {
  context: string;
  icon: React.ReactElement;
  title: string;
  items: MenuItemDef[];
  header?: string | null;
  onItem: (item: MenuItemDef) => void;
}) {
  const flat = flattenMenuItems(items);
  return (
    <Menu positioning="below-end">
      <MenuTrigger disableButtonEnhancement>
        <Button appearance="transparent" icon={icon} title={title} aria-label={title} />
      </MenuTrigger>
      <MenuPopover>
        <MenuList data-mw-menu={context}>
          {header && (
            <>
              <div
                title={header}
                style={{
                  padding: "6px 12px",
                  fontSize: "0.75rem",
                  fontWeight: 600,
                  textTransform: "uppercase",
                  letterSpacing: "0.05em",
                  color: "var(--colorNeutralForeground3)",
                  whiteSpace: "nowrap",
                  overflow: "hidden",
                  textOverflow: "ellipsis",
                  maxWidth: 260,
                }}
              >
                {header}
              </div>
              <MenuDivider />
            </>
          )}
          {flat.length === 0 && (
            <div style={{ padding: "6px 12px", fontSize: "0.8rem", color: "var(--colorNeutralForeground3)" }}>
              No actions available
            </div>
          )}
          {flat.map((item, i) =>
            item.area === "_separator" ? (
              <MenuDivider key={`sep-${i}`} />
            ) : (
              <MenuItem key={`${item.area}-${item.label}-${i}`} title={item.tooltip ?? item.label} onClick={() => onItem(item)}>
                <MenuItemIcon icon={item.icon} />
                {item.label}
              </MenuItem>
            ),
          )}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

export function HeaderMenus() {
  const live = useLiveConnection();
  const nav = useNavigationState();
  const router = useRouter();
  const sidePanel = useSidePanel();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;

  const source = live.state.kind === "live" && nav.target ? live.getAreaSource(nav.target) : null;
  const nodeItems = useMenuItems(source, "Node");
  const meshItems = useMenuItems(source, "Mesh");
  const aiItems = useMenuItems(source, "AI");
  // GitHub keeps its own dropdown — populated only when the Space has a repository configured
  // (the server provider self-gates); hidden entirely when empty. (Instance sync is in the Node
  // menu as "Synchronizations", not a separate dropdown.)
  const gitHubItems = useMenuItems(source, "GitHub");

  const currentAddress = nav.target?.address ?? "";

  // The current node's display name — headlines the Node/Mesh menus (Blazor CurrentNodeName).
  const [nodeName, setNodeName] = useState<string | null>(null);
  useEffect(() => {
    setNodeName(null);
    if (!mesh || !currentAddress) return;
    let liveFlag = true;
    mesh.getNode(currentAddress).then((node) => {
      if (!liveFlag || !node) return;
      setNodeName(str(node.name) || str(node.id) || null);
    });
    return () => {
      liveFlag = false;
    };
  }, [mesh, currentAddress]);

  const handleItem = useCallback(
    (item: MenuItemDef) => {
      // Imperative actions (no Href): the AI menu's "New thread" opens the chat panel fresh.
      if (item.area === AI_NEW_THREAD_ACTION) {
        sidePanel.openNewThread();
        return;
      }
      if (item.href) {
        router.push(item.href);
        return;
      }
      router.push(currentAddress ? `/${currentAddress}/${item.area}` : `/${item.area}`);
    },
    [router, currentAddress, sidePanel],
  );

  const navigateToSettings = useCallback(() => {
    if (currentAddress) {
      // Per-node settings — governed by the node's own RLS.
      router.push(`/${currentAddress}/Settings`);
      return;
    }
    if (!mesh) return;
    // Root → GlobalSettings is ADMIN-ONLY. Probe with an Admin-partition read: RLS returns rows
    // only to platform admins (the browser twin of gating on hub.IsGlobalAdmin), so a non-admin
    // never issues the GlobalSettings subscribe (the access-denied resubscribe hazard).
    mesh.queryNodes("path:Admin/_Access nodeType:AccessAssignment scope:children limit:1", 1).then((rows) => {
      if (rows.length > 0) router.push("/GlobalSettings");
      else if (mesh.userId) router.push(`/User/${mesh.userId}`);
      else router.push("/");
    });
  }, [mesh, currentAddress, router]);

  if (!mesh) return null;

  return (
    <>
      <MenuButton
        context="Node"
        icon={<Cube20Regular />}
        title="Node menu"
        items={nodeItems}
        header={nodeName}
        onItem={handleItem}
      />
      <MenuButton
        context="Mesh"
        icon={<Grid20Regular />}
        title="Mesh menu"
        items={meshItems}
        header={nodeName}
        onItem={handleItem}
      />
      <MenuButton context="AI" icon={<Sparkle20Regular />} title="AI" items={aiItems} onItem={handleItem} />
      {gitHubItems.length > 0 && (
        <MenuButton
          context="GitHub"
          icon={<BranchFork20Regular />}
          title="GitHub"
          items={gitHubItems}
          onItem={handleItem}
        />
      )}
      <Button
        appearance="transparent"
        icon={<Settings20Regular />}
        title="Settings"
        aria-label="Settings"
        onClick={navigateToSettings}
      />
    </>
  );
}
