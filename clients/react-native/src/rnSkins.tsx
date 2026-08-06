// The RN skin pack — native ports of the layout skins the web pack renders in
// clients/react/src/render/skins.tsx, which in turn mirror Blazor's skinned views.
//
// These were the thirteen skins the RN pack silently lacked (BodyContent, EditForm, Editor, Footer,
// Header, LayoutGridItem, Main, MenuItem, Property, Splitter, SplitterPane, Tab, Tabs). An
// unregistered skin falls through to `__default` (a passthrough), which drops the skin's semantics:
// a LayoutGridItem lost its column span, a Tabs container rendered every tab stacked at once, and a
// Property field lost its label. Nothing failed — there was no RN parity ratchet until now.

import { useState } from "react";
import { View, Text, Pressable, StyleSheet, useWindowDimensions } from "react-native";
import {
  ControlRenderer,
  RenderArea,
  useChildAreas,
  useResolve,
  str,
  type Skin,
  type SkinComponent,
  type UiControl,
} from "@meshweaver/react/core";

const s = str;

/** Renders a container's child areas (each in its own scope) — the shared body of every layout skin. */
function Children({ control }: { control: UiControl }) {
  const children = useChildAreas(control);
  return (
    <>
      {children.map((c, i) => (
        <RenderArea key={c.key || i} areaKey={c.key} />
      ))}
    </>
  );
}

// ── semantic wrappers ────────────────────────────────────────────────────────
// Main/Header/Footer/BodyContent are semantic HTML on the web (<main>, <header>, …). RN has no
// document semantics, so they become plain Views carrying the matching accessibility role — the
// screen-reader affordance is what the semantic tag actually buys.
function semanticWrapper(role: "header" | "footer" | "main" | "section"): SkinComponent {
  return function SemanticSkin({ control }) {
    return (
      <View accessibilityRole={role === "section" ? "summary" : (role as never)} style={styles.column}>
        <ControlRenderer control={control} />
      </View>
    );
  };
}

/** Plain vertical layout — the Layout / Editor skins. */
const PlainLayoutSkin: SkinComponent = ({ control }) => (
  <View style={styles.column}>
    <Children control={control} />
  </View>
);

// ── LayoutGridItem ───────────────────────────────────────────────────────────
const GRID_BREAKPOINTS: Record<string, number> = { xs: 0, sm: 600, md: 960, lg: 1280, xl: 1920, xxl: 2560 };

/**
 * The per-item column span of a LayoutGrid (Xs/Sm/Md/Lg/Xl/Xxl, columns out of 12) — the
 * FluentGridItem breakpoints Blazor applies. Mobile-first: the value set at the LARGEST breakpoint
 * at or below the current width wins; nothing set ⇒ full row.
 *
 * The web pack expresses this with CSS classes + media queries; RN has neither, so the breakpoint is
 * resolved against the live window width and turned into a percentage width.
 */
export function gridItemSpan(skin: Record<string, unknown>, width: number): number {
  let span = 12; // nothing set → full row, matching .mw-grid-item's default
  let bestMin = -1;
  for (const [bp, minWidth] of Object.entries(GRID_BREAKPOINTS)) {
    if (width < minWidth) continue; // breakpoint not reached
    const raw = Number(skin[bp]);
    if (!Number.isInteger(raw) || raw < 1 || raw > 12) continue;
    if (minWidth >= bestMin) {
      bestMin = minWidth;
      span = raw;
    }
  }
  return span;
}

const GridItemSkin: SkinComponent = ({ skin, control }) => {
  const { width } = useWindowDimensions();
  const span = gridItemSpan(skin as unknown as Record<string, unknown>, width);
  return (
    <View style={{ width: `${(span / 12) * 100}%`, minWidth: 0 }}>
      <ControlRenderer control={control} />
    </View>
  );
};

// ── Tabs ─────────────────────────────────────────────────────────────────────
/** A child's tab label comes from its own Tab skin (Skin.Label), as in the web pack. */
export function tabLabel(control?: UiControl): string | undefined {
  const skin = control?.skins?.find((sk: Skin) => /tab/i.test(String(sk.$type)));
  return skin?.label != null ? String(skin.label) : undefined;
}

const TabsSkin: SkinComponent = ({ skin, control }) => {
  const children = useChildAreas(control);
  const tabs = children.map((c, i) => ({
    value: String(c.named.id ?? i),
    key: c.key,
    label: tabLabel(c.control) ?? String(c.named.id ?? `Tab ${i + 1}`),
  }));
  const [selected, setSelected] = useState<string>(String(skin.activeTabId ?? tabs[0]?.value ?? "0"));
  const active = tabs.find((t) => t.value === selected) ?? tabs[0];
  return (
    <View style={styles.column}>
      <View style={styles.tabBar}>
        {tabs.map((t) => (
          <Pressable
            key={t.value}
            accessibilityRole="tab"
            accessibilityState={{ selected: t.value === active?.value }}
            onPress={() => setSelected(t.value)}
            style={[styles.tab, t.value === active?.value && styles.tabActive]}
          >
            <Text style={[styles.tabText, t.value === active?.value && styles.tabTextActive]}>{t.label}</Text>
          </Pressable>
        ))}
      </View>
      {active ? <RenderArea areaKey={active.key} /> : null}
    </View>
  );
};

// ── Splitter ─────────────────────────────────────────────────────────────────
/** Parse a pixel size ("280px", "280", 280) to a number; `null` for star / percentage / unset. */
function parsePx(v: unknown): number | null {
  if (v == null) return null;
  if (typeof v === "number") return Number.isFinite(v) ? v : null;
  const t = String(v).trim();
  const m = /^(-?\d*\.?\d+)\s*px$/i.exec(t) ?? /^(-?\d*\.?\d+)$/.exec(t);
  return m ? parseFloat(m[1]) : null;
}

export interface PaneSpec {
  fixedPx: number | null;
  grow: number;
}

/**
 * A pane's sizing spec, mirroring SplitterPaneSkin.Size semantics: a definite PIXEL width ("280px" →
 * `fixedPx`) or a STAR weight ("*", "2*", or unspecified → `grow`, filling the remainder). Ported
 * verbatim from the web pack so a fixed 280px menu stays 280px on both platforms rather than
 * becoming a 280:1 star weight (≈ the whole width).
 */
export function paneSpec(control?: UiControl): PaneSpec {
  const skin = control?.skins?.find((sk: Skin) => /splitterpane/i.test(String(sk.$type)));
  const size = skin?.size;
  const t = size == null ? "" : String(size).trim();
  const star = /^(\d*\.?\d*)\*$/.exec(t); // "*", "2*", "1.5*"
  if (t === "" || t === "*" || star) {
    const w = star && star[1] ? parseFloat(star[1]) : 1;
    return { fixedPx: null, grow: w > 0 ? w : 1 };
  }
  const px = parsePx(size);
  if (px != null) return { fixedPx: px, grow: 0 };
  return { fixedPx: null, grow: 1 }; // e.g. "50%" → still fills
}

/**
 * Panes laid out along the skin's orientation, each sized per its SplitterPaneSkin.
 *
 * The web port also renders DRAGGABLE gutters (Blazor's FluentMultiSplitter). RN deliberately
 * renders the panes without a drag handle: a drag gutter is a pointer affordance, and the native
 * idiom for a phone-sized viewport is a fixed split. The SIZING contract — which is what determines
 * whether the layout is correct — is identical.
 */
const SplitterSkin: SkinComponent = ({ skin, control }) => {
  const children = useChildAreas(control);
  const horizontal = s(skin.orientation).toLowerCase() !== "vertical";
  return (
    <View style={{ flexDirection: horizontal ? "row" : "column", flex: 1, minHeight: 0 }}>
      {children.map((c, i) => {
        const spec = paneSpec(c.control);
        const sizing =
          spec.fixedPx != null
            ? { flexGrow: 0, flexShrink: 0, flexBasis: spec.fixedPx }
            : { flexGrow: spec.grow, flexShrink: 1, flexBasis: 0 };
        return (
          <View key={c.key || i} style={[sizing, { minWidth: 0, minHeight: 0 }]}>
            <RenderArea areaKey={c.key} />
          </View>
        );
      })}
    </View>
  );
};

// ── MenuItem ─────────────────────────────────────────────────────────────────
/**
 * A menu entry: icon + label, pressable, with an expandable sub-menu when the control carries child
 * areas — the native shape of the web pack's MenuItemSkin (which renders a Fluent split button plus
 * an absolutely-positioned dropdown). RN expands INLINE rather than overlaying, the standard native
 * disclosure pattern.
 */
const MenuItemSkin: SkinComponent = ({ skin, control }) => {
  const [open, setOpen] = useState(false);
  const children = useChildAreas(control);
  const hasSubMenus = children.length > 0;
  const label = s(useResolve(skin.title ?? skin.label ?? control.title));
  const icon = s(useResolve(skin.icon ?? control.icon));
  return (
    <View>
      <Pressable
        accessibilityRole="menuitem"
        onPress={() => hasSubMenus && setOpen((o) => !o)}
        style={styles.menuRow}
      >
        {icon ? <Text style={styles.menuIcon}>{icon}</Text> : null}
        <Text style={styles.menuLabel}>{label}</Text>
        {hasSubMenus ? <Text style={styles.menuChevron}>{open ? "▾" : "▸"}</Text> : null}
      </Pressable>
      {hasSubMenus && open ? (
        <View style={styles.subMenu}>
          <Children control={control} />
        </View>
      ) : null}
    </View>
  );
};

// ── Property / EditForm ──────────────────────────────────────────────────────
/**
 * The per-field wrapper of an EditForm, mirroring Blazor's PropertyView: a label (Skin.Label,
 * falling back to Name/Title), an optional description line, then the bound field control.
 */
const PropertySkin: SkinComponent = ({ skin, control }) => {
  const label = useResolve(skin.label ?? skin.name ?? skin.title);
  const description = useResolve(skin.description);
  return (
    <View style={{ gap: 2 }}>
      {label != null && String(label).length > 0 ? <Text style={styles.propertyLabel}>{String(label)}</Text> : null}
      {description != null && String(description).length > 0 ? (
        <Text style={styles.propertyDescription}>{String(description)}</Text>
      ) : null}
      <ControlRenderer control={control} />
    </View>
  );
};

/**
 * The form wrapper. Fields data-bind per-edit through the standard update event — the owning hub
 * persists every change, so there is no explicit submit, exactly as in Blazor's EditFormView.
 */
const EditFormSkin: SkinComponent = ({ control }) => (
  <View style={{ flexDirection: "column", gap: 12 }}>
    <Children control={control} />
  </View>
);

const passthrough: SkinComponent = ({ control }) => <ControlRenderer control={control} />;

/**
 * The skins the RN pack was missing. Merged into rnPack.skins on top of the ones already there
 * (LayoutStack, Layout, LayoutGrid, Card, NavMenu, NavGroup, Toolbar).
 *
 * SplitterPane is deliberately absent: the parent SplitterSkin reads it (paneSpec) to size each
 * pane, exactly as Blazor's FluentMultiSplitterPane does — a registry entry would double-wrap it.
 */
export const rnSkins: Record<string, SkinComponent> = {
  LayoutGridItem: GridItemSkin,
  Tabs: TabsSkin,
  Tab: passthrough,
  Splitter: SplitterSkin,
  MenuItem: MenuItemSkin,
  Property: PropertySkin,
  EditForm: EditFormSkin,
  Editor: PlainLayoutSkin,
  Main: semanticWrapper("main"),
  Header: semanticWrapper("header"),
  Footer: semanticWrapper("footer"),
  BodyContent: semanticWrapper("section"),
};

const styles = StyleSheet.create({
  column: { flexDirection: "column", gap: 8 },
  tabBar: { flexDirection: "row", flexWrap: "wrap", borderBottomWidth: 1, borderColor: "#e1e1e1" },
  tab: { paddingVertical: 8, paddingHorizontal: 12, borderBottomWidth: 2, borderColor: "transparent" },
  tabActive: { borderColor: "#0f6cbd" },
  tabText: { fontSize: 14, color: "#616161" },
  tabTextActive: { color: "#0f6cbd", fontWeight: "600" },
  menuRow: { flexDirection: "row", alignItems: "center", gap: 8, paddingVertical: 10, paddingHorizontal: 8 },
  menuIcon: { fontSize: 15 },
  menuLabel: { flex: 1, fontSize: 14, color: "#242424" },
  menuChevron: { fontSize: 12, color: "#616161" },
  subMenu: { paddingLeft: 16, borderLeftWidth: 1, borderColor: "#e1e1e1" },
  propertyLabel: { fontSize: 13, fontWeight: "600", color: "#242424" },
  propertyDescription: { fontSize: 12, color: "#616161" },
});
