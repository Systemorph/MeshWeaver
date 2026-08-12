// The shell's left menu — the mesh's $Menu:{context} streams plus the in-app client contexts.
//
// Split out of Shell.tsx so it can be unit-tested: Shell imports ./connection (→ expo-constants),
// which needs the Expo runtime to even load, and the drill-down below is worth pinning on its own.
// This module deliberately imports nothing beyond react-native + the theme, so the client contexts
// arrive as a PROP rather than through ./screens (which also pulls ./connection).

import { useState, type ReactNode } from "react";
import { View, Text, Pressable, ScrollView } from "react-native";
import type { AreaTree } from "@meshweaver/react/core";
import { type NavTarget } from "./nav";
import { useStyles, type Palette } from "./theme";
import { StyleSheet } from "react-native";

const useSheet = () => useStyles(makeStyles);

/** A node-menu entry off the wire — NodeMenuItemDefinition, camelCase. */
export interface MenuItem {
  label?: string;
  href?: string;
  area?: string;
  order?: number;
  icon?: string;
  tooltip?: string;
  /** Nested entries — NodeMenuItemDefinition.Children. */
  children?: MenuItem[];
}

/** NodeMenuItemDefinition.SeparatorArea — a divider, never activatable. */
export const SEPARATOR_AREA = "_separator";

/**
 * NodeMenuItemDefinition.GroupAreaPrefix — a pure grouping parent. A prefix, not one shared sentinel,
 * because `area` is also the key the MenuPresentation catalog matches on: each group needs its own key
 * to stay addressable (`_group:Export`).
 */
export const GROUP_AREA_PREFIX = "_group:";

/** True when `area` marks a pure grouping parent. */
export const isGroupArea = (area: string | undefined): boolean =>
  area != null && area.startsWith(GROUP_AREA_PREFIX);

/**
 * A submenu parent. Any entry carrying children is one, and it is NEVER activatable — the same rule
 * the web clients apply (NodeMenuItemDefinition.IsSubmenuParent).
 */
export function isSubmenuParent(it: MenuItem): boolean {
  return (it.children != null && it.children.length > 0) || isGroupArea(it.area);
}

/** Ascending by `order` — the wire's sort key at every depth. */
export function sortByOrder(items: MenuItem[]): MenuItem[] {
  return [...items].sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
}

export function menuItems(tree: AreaTree, key: string): MenuItem[] {
  const items = (tree.areas?.[key] as { items?: MenuItem[] } | undefined)?.items ?? [];
  return sortByOrder(items);
}

/** A context section: one $Menu:{key} stream rendered under a glyph + label. */
export interface MeshContext {
  key: string;
  label: string;
  glyph: string;
}

/** An in-app destination section (You: profile / voice / connect). */
export interface ClientMenu<D> {
  context: string;
  glyph: string;
  items: { destination: D; label: string }[];
}

/**
 * The left menu.
 *
 * 🚨 Nested entries DRILL DOWN here; they do not fly out. A submenu that opens a second panel beside
 * the first is a pointer-and-large-screen idiom — on a phone there is no hover, the panel is already
 * narrow, and a nested one either covers its parent or runs off the viewport. Tapping a parent
 * REPLACES the list with its children and offers a back control, so exactly one level is on screen at
 * a time (the shape the MAUI shell already uses). The web clients keep the conventional nested
 * flyout: parity across clients means equivalent CAPABILITY, not an identical gesture.
 */
export function LeftMenuView<D>({
  tree,
  nav,
  home,
  contexts,
  clientMenus,
  clientScreen,
  onNavigate,
  onClientScreen,
}: {
  tree: AreaTree;
  nav: NavTarget;
  home: NavTarget;
  contexts: MeshContext[];
  clientMenus: ClientMenu<D>[];
  clientScreen: D | null;
  onNavigate: (t: NavTarget) => void;
  onClientScreen: (d: D | null) => void;
}): ReactNode {
  const styles = useSheet();

  // The chain of parents entered so far, so nesting deeper than one level backs out one step at a
  // time rather than jumping straight to the root.
  const [drill, setDrill] = useState<MenuItem[]>([]);
  const current = drill.length > 0 ? drill[drill.length - 1] : null;

  const activate = (it: MenuItem) => {
    if (isSubmenuParent(it)) {
      setDrill((d) => [...d, it]);
      return;
    }
    if (it.area) onNavigate({ address: nav.address, area: it.area });
  };

  const rowLabel = (it: MenuItem) => `${it.icon ? `${it.icon}  ` : ""}${it.label ?? it.area ?? ""}`;

  const entry = (it: MenuItem, i: number) =>
    it.area === SEPARATOR_AREA ? (
      <View key={i} style={styles.menuSeparator} />
    ) : (
      <NavRow
        key={i}
        label={rowLabel(it)}
        active={!clientScreen && nav.area === it.area}
        onPress={() => activate(it)}
        chevron={isSubmenuParent(it)}
        accessibilityLabel={it.tooltip ?? it.label ?? it.area ?? ""}
        opensSubmenu={isSubmenuParent(it)}
      />
    );

  if (current) {
    return (
      <View style={styles.left}>
        <ScrollView contentContainerStyle={{ paddingVertical: 10 }}>
          {/* Back returns ONE level; the parent's own label titles the child view, so it is always
              clear where you are. */}
          <NavRow
            label="‹  Back"
            active={false}
            onPress={() => setDrill((d) => d.slice(0, -1))}
            accessibilityLabel={`Back to ${drill.length > 1 ? (drill[drill.length - 2].label ?? "menu") : "menu"}`}
          />
          <Text style={styles.sectionLabel}>{rowLabel(current)}</Text>
          {sortByOrder(current.children ?? []).map(entry)}
        </ScrollView>
      </View>
    );
  }

  return (
    <View style={styles.left}>
      <ScrollView contentContainerStyle={{ paddingVertical: 10 }}>
        <NavRow label="⌂  Home" active={!clientScreen && nav.address === home.address} onPress={() => onNavigate(home)} />

        {contexts.map((ctx) => {
          const items = menuItems(tree, ctx.key);
          if (items.length === 0) return null;
          return (
            <View key={ctx.key}>
              <Text style={styles.sectionLabel}>{ctx.glyph}  {ctx.label}</Text>
              {items.map(entry)}
            </View>
          );
        })}

        {clientMenus.map((ctx) => (
          <View key={ctx.context}>
            <Text style={styles.sectionLabel}>{ctx.glyph}  {ctx.context}</Text>
            {ctx.items.map((it) => (
              <NavRow
                key={String(it.destination)}
                label={it.label}
                active={clientScreen === it.destination}
                onPress={() => onClientScreen(it.destination)}
              />
            ))}
          </View>
        ))}
      </ScrollView>
    </View>
  );
}

/**
 * One menu row. The WHOLE row is the hit area (never just the glyph) and it clears the 44pt minimum
 * touch target. `chevron` marks a row that drills in rather than acting, so a parent reads as leading
 * somewhere before it is tapped; a screen reader gets the same fact from the `menu` role.
 */
export function NavRow({
  label,
  active,
  onPress,
  chevron,
  accessibilityLabel,
  opensSubmenu,
}: {
  label: string;
  active: boolean;
  onPress: () => void;
  chevron?: boolean;
  accessibilityLabel?: string;
  opensSubmenu?: boolean;
}): ReactNode {
  const styles = useSheet();
  return (
    <Pressable
      style={({ hovered }: any) => [styles.navItem, hovered && styles.navItemHover, active && styles.navItemActive]}
      onPress={onPress}
      accessibilityRole={opensSubmenu ? "menu" : "menuitem"}
      accessibilityLabel={accessibilityLabel ?? label}
      accessibilityState={opensSubmenu ? { expanded: false } : { selected: active }}
    >
      <Text style={[styles.navItemText, active && styles.navItemTextActive]} numberOfLines={1}>{label}</Text>
      {chevron ? <Text style={styles.navChevron}>›</Text> : null}
    </Pressable>
  );
}

const makeStyles = (p: Palette) =>
  StyleSheet.create({
    left: { width: 236, flexGrow: 0, flexShrink: 0, backgroundColor: p.sidebarBg, borderRightWidth: 1, borderRightColor: p.border },
    sectionLabel: { fontSize: 11, fontWeight: "700", color: p.textMuted, letterSpacing: 0.5, textTransform: "uppercase", paddingHorizontal: 16, marginTop: 14, marginBottom: 6 },
    // minHeight 44 = the platform minimum touch target; the row is flex so the label and the drill-in
    // chevron sit on one line and the WHOLE row stays tappable.
    navItem: { flexDirection: "row", alignItems: "center", minHeight: 44, paddingHorizontal: 16, paddingVertical: 7, marginHorizontal: 8, borderRadius: 6 },
    navItemHover: { backgroundColor: p.navHover },
    navItemActive: { backgroundColor: p.navActiveBg },
    navItemText: { flexShrink: 1, fontSize: 13.5, color: p.text },
    navItemTextActive: { color: p.navActiveText, fontWeight: "600" },
    navChevron: { marginLeft: "auto", paddingLeft: 8, fontSize: 16, color: p.textMuted },
    menuSeparator: { height: StyleSheet.hairlineWidth, backgroundColor: p.border, marginHorizontal: 16, marginVertical: 6 },
  });
