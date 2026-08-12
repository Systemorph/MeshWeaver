"use client";

// The header's Node / Mesh / AI menus + the Settings gear — the React port of the Blazor shell's
// PortalLayoutBase header (src/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor):
//
//   - Menus are MESH-DRIVEN, never hardcoded: the node hub's RenderMenus renderer writes a
//     permission-filtered MenuControl into the $Menu:{Node|Mesh|AI} slots of the SAME layout-area
//     stream the page renders — this component reads those slots off the current page's live
//     AreaSource (the React twin of MenuStreamExtensions.GetMenu / IMenuItemsProvider).
//   - Hierarchical items render as nested Fluent sub-menus (MenuEntries, the twin of Blazor's
//     NodeMenuItemList); the current node's name headlines the Node and Mesh menus.
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
import { useLocalize } from "@meshweaver/react";

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

/** NodeMenuItemDefinition.SeparatorArea — a divider, never activatable. */
export const SEPARATOR_AREA = "_separator";

/**
 * NodeMenuItemDefinition.GroupAreaPrefix — a pure GROUPING parent: it exists only to hold `children`
 * and has nowhere of its own to go. Rendered as a submenu, never activated.
 *
 * A prefix, not one shared sentinel, because `area` is also the key the MenuPresentation catalog
 * matches on — each group needs its own key to stay addressable (`_group:Export`).
 */
export const GROUP_AREA_PREFIX = "_group:";

/** True when `area` marks a pure grouping parent. */
export const isGroupArea = (area: string | undefined): boolean =>
  area != null && area.startsWith(GROUP_AREA_PREFIX);

/**
 * A submenu parent. Any entry carrying children is one — its own area/href is deliberately ignored
 * for activation, because Fluent v9 (like FAST on the Blazor side) makes a `MenuTrigger`-wrapped
 * item toggle its submenu rather than invoke. Mirrors NodeMenuItemDefinition.IsSubmenuParent.
 */
export function isSubmenuParent(item: MenuItemDef): boolean {
  return (item.children != null && item.children.length > 0) || isGroupArea(item.area);
}

/** Ascending by `order` — the wire's sort key at every depth. */
function sortByOrder(items: MenuItemDef[]): MenuItemDef[] {
  return [...items].sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
}

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

/**
 * Renders one level of the menu, recursing into `children` as NATIVE Fluent v9 sub-menus.
 *
 * This replaces `flattenMenuItems`, which mirrored Blazor's `FlattenMenuItems`: it DELETED the parent
 * and spliced its children inline behind a divider, throwing away the label/icon/tooltip that told
 * them apart (with two GitHub sync sources you got two identical action triplets and no way to know
 * which repo either belonged to).
 *
 * The nesting is the component library's own: a `<Menu>` whose `<MenuTrigger>` wraps a `<MenuItem>`
 * inside the parent `<MenuList>` is Fluent v9's documented submenu shape, so roles, `aria-haspopup` /
 * `aria-expanded`, and the keyboard model (Enter / ArrowRight to open, ArrowLeft / Escape to close)
 * all come from Fluent. A parent gets NO onClick — it opens, it never navigates.
 *
 * Depth is unbounded — the function recurses into itself.
 */
export function MenuEntries({
  items,
  onItem,
}: {
  items: MenuItemDef[];
  onItem: (item: MenuItemDef) => void;
}) {
  return (
    <>
      {items.map((item, i) => {
        if (item.area === SEPARATOR_AREA) return <MenuDivider key={`sep-${i}`} />;
        const key = `${item.area}-${item.label}-${i}`;
        if (isSubmenuParent(item)) {
          return (
            <Menu key={key}>
              <MenuTrigger disableButtonEnhancement>
                <MenuItem title={item.tooltip ?? item.label}>
                  <MenuItemIcon icon={item.icon} />
                  {item.label}
                </MenuItem>
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {/* Sorted here as well as on the server: `order` must mean the same thing at
                      every depth, and the client is the last place that can guarantee it. */}
                  <MenuEntries items={sortByOrder(item.children ?? [])} onItem={onItem} />
                </MenuList>
              </MenuPopover>
            </Menu>
          );
        }
        return (
          <MenuItem key={key} title={item.tooltip ?? item.label} onClick={() => onItem(item)}>
            <MenuItemIcon icon={item.icon} />
            {item.label}
          </MenuItem>
        );
      })}
    </>
  );
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
          {items.length === 0 && (
            <div style={{ padding: "6px 12px", fontSize: "0.8rem", color: "var(--colorNeutralForeground3)" }}>
              No actions available
            </div>
          )}
          <MenuEntries items={items} onItem={onItem} />
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

export function HeaderMenus() {
  const t = useLocalize();
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
        title={t("menu.node")}
        items={nodeItems}
        header={nodeName}
        onItem={handleItem}
      />
      <MenuButton
        context="Mesh"
        icon={<Grid20Regular />}
        title={t("menu.mesh")}
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
        title={t("common.settings")}
        aria-label={t("common.settings")}
        onClick={navigateToSettings}
      />
    </>
  );
}
