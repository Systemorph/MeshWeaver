// The app shell — an Outlook-for-macOS-style chrome around the live layout area:
//   ┌───────────────────────────────────────────────┐
//   │ top bar:  ◆ MeshWeaver   [ search … ]      ◍   │
//   ├───────────────────────────────────────────────┤
//   │ toolbar:  ⌂ Home  ›  Doc  ›  Architecture      │   (breadcrumb navigation)
//   ├──────────┬──────────────────────────┬──────────┤
//   │ left     │  main content            │ right    │
//   │ menus    │  (.markdown-body doc     │ sidebar  │
//   │ (from    │   OR a client screen)    │ (on this │
//   │ providers)│                          │  page)   │
//   └──────────┴──────────────────────────┴──────────┘
//
// The left menus are NOT hardcoded — they come from the mesh's menu providers, streamed into the
// layout store as $Menu:{context} (Node / Mesh / AI; see NodeMenuItemsExtensions), plus the in-app
// client contexts (You: profile / voice / connect) — the same split the MAUI PortalShellPage renders.
import { useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import { View, Text, TextInput, Pressable, ScrollView, StyleSheet, useWindowDimensions } from "react-native";
import { RenderArea, type AreaSource, type AreaTree } from "@meshweaver/react/core";
import { areaErrorMessage } from "./areaError";
import { type NavTarget } from "./nav";
import { CLIENT_MENUS, ClientScreen, type ClientDestination } from "./screens";
import { loadInstances, currentInstance, setCurrentInstance, instanceIdentity, type InstanceIdentity } from "./connection";
import { useStyles, useTheme, type Palette } from "./theme";

const useSheet = () => useStyles(makeStyles);

const CONTENT_AREA = "Overview";
// The startup / home landing. On an anonymous local mesh there is no personal user page, so we land on
// the documentation home; connected to a portal with a signed-in identity this is where the user's own
// node/area would go (the MAUI app lands on device-user/Activity).
export const HOME: NavTarget = { address: "Doc/Architecture", area: CONTENT_AREA };

// The mesh menu contexts streamed as $Menu:{context}, in display order, with a glyph like MAUI's.
const MESH_CONTEXTS: { key: string; label: string; glyph: string }[] = [
  { key: "$Menu:Mesh", label: "Mesh", glyph: "▦" },
  { key: "$Menu:Node", label: "This node", glyph: "🧊" },
  { key: "$Menu:AI", label: "AI", glyph: "✨" },
];

interface MenuItem {
  label?: string;
  href?: string;
  area?: string;
  order?: number;
}

function useTree(source: AreaSource): AreaTree {
  return useSyncExternalStore(source.subscribe, source.getState, source.getState);
}

/** The live source's error string, reactively (set when the area subscription faults / ends before a
 *  snapshot). Lets the shell surface a denied/gone area instead of a silent blank pane. */
function useAreaError(source: AreaSource): string | null {
  const read = () => source.error ?? null;
  return useSyncExternalStore(source.subscribe, read, read);
}

function menuItems(tree: AreaTree, key: string): MenuItem[] {
  const items = (tree.areas?.[key] as { items?: MenuItem[] } | undefined)?.items ?? [];
  return [...items].sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
}

export function Shell({
  source,
  nav,
  clientScreen,
  onNavigate,
  onClientScreen,
  onReconnect,
}: {
  source: AreaSource;
  nav: NavTarget;
  clientScreen: ClientDestination | null;
  onNavigate: (t: NavTarget) => void;
  onClientScreen: (d: ClientDestination | null) => void;
  onReconnect: () => void;
}): ReactNode {
  const tree = useTree(source);
  const styles = useSheet();
  // Responsive: the fixed left menu (236) + right TOC (250) don't fit a phone — the content column would
  // be squeezed to zero width. On a narrow viewport the menu becomes a slide-over (hamburger in the top
  // bar), the TOC is hidden, and the content takes the FULL width. Wide viewports keep the 3-column shell.
  const { width } = useWindowDimensions();
  const isMobile = width < 760;
  const [menuOpen, setMenuOpen] = useState(false);
  const navigate = (t: NavTarget) => { setMenuOpen(false); onNavigate(t); };
  const clientNav = (d: ClientDestination | null) => { setMenuOpen(false); onClientScreen(d); };

  const menu = (
    <LeftMenu tree={tree} nav={nav} clientScreen={clientScreen} onNavigate={navigate} onClientScreen={clientNav} />
  );
  const main = clientScreen ? (
    <View style={styles.content}>
      <ClientScreen destination={clientScreen} onConnected={onReconnect} />
    </View>
  ) : (
    <ContentPane source={source} nav={nav} onNavigate={onNavigate} />
  );

  return (
    <View style={styles.root}>
      <TopBar
        onNavigate={onNavigate}
        isMobile={isMobile}
        onToggleMenu={() => setMenuOpen((o) => !o)}
        onReconnect={onReconnect}
        onManageInstances={() => onClientScreen("instances")}
      />
      <Breadcrumb nav={nav} clientScreen={clientScreen} onNavigate={onNavigate} />
      <View style={styles.body}>
        {isMobile ? (
          <>
            {main}
            {menuOpen ? (
              <>
                <Pressable style={styles.scrim} onPress={() => setMenuOpen(false)} />
                <View style={styles.drawer}>{menu}</View>
              </>
            ) : null}
          </>
        ) : (
          <>
            {menu}
            {main}
            <RightSidebar contentKey={clientScreen ?? `${nav.address}/${nav.area}`} />
          </>
        )}
      </View>
    </View>
  );
}

// ── top bar ───────────────────────────────────────────────────────────────────
function TopBar({ onNavigate, isMobile, onToggleMenu, onReconnect, onManageInstances }: { onNavigate: (t: NavTarget) => void; isMobile: boolean; onToggleMenu: () => void; onReconnect: () => void; onManageInstances: () => void }): ReactNode {
  const [q, setQ] = useState("");
  const styles = useSheet();
  const { mode, toggle, palette } = useTheme();
  // The env color tints the top bar's bottom edge so the whole chrome signals WHICH mesh you're on.
  const envColor = instanceIdentity(currentInstance()).color;
  return (
    <View style={[styles.topbar, { borderBottomColor: envColor, borderBottomWidth: 3 }]}>
      {isMobile ? (
        <Pressable style={styles.hamburger} onPress={onToggleMenu} accessibilityLabel="Menu">
          <Text style={styles.hamburgerText}>☰</Text>
        </Pressable>
      ) : null}
      <Pressable style={isMobile ? styles.brandMobile : styles.brand} onPress={() => onNavigate(HOME)}>
        <View style={styles.logo}><Text style={styles.logoMark}>◆</Text></View>
        {isMobile ? null : <Text style={styles.brandText}>MeshWeaver</Text>}
      </Pressable>
      <InstanceSwitcher isMobile={isMobile} onReconnect={onReconnect} onManage={onManageInstances} />
      <View style={styles.searchWrap}>
        <Text style={styles.searchIcon}>⌕</Text>
        <TextInput
          style={styles.search}
          value={q}
          onChangeText={setQ}
          placeholder="Search or go to a path (e.g. Doc/Architecture)"
          placeholderTextColor={palette.textMuted}
          onSubmitEditing={() => {
            const a = q.trim().replace(/^\/+/, "");
            if (a) onNavigate({ address: a, area: CONTENT_AREA });
          }}
          returnKeyType="go"
        />
      </View>
      <Pressable style={({ hovered }: any) => [styles.themeBtn, hovered && styles.themeBtnHover]} onPress={toggle} accessibilityLabel="Toggle theme">
        <Text style={styles.themeBtnText}>{mode === "dark" ? "☀" : "☾"}</Text>
      </Pressable>
      <View style={styles.avatar}><Text style={styles.avatarText}>R</Text></View>
    </View>
  );
}

// ── instance / environment switcher ──────────────────────────────────────────────
// A top-bar pill showing the CURRENT environment (icon + name, tinted + typeset by env), opening a
// menu to switch between all instances and to add/manage more. Switching re-dials the live source
// (onReconnect) and returns Home. Icons + typesetting differ per env class (see instanceIdentity):
// prod/client are UPPERCASE + heavy, local is calm — so you always know which mesh you're pointed at.
function InstanceSwitcher({ isMobile, onReconnect, onManage }: { isMobile: boolean; onReconnect: () => void; onManage: () => void }): ReactNode {
  const styles = useSheet();
  const [open, setOpen] = useState(false);
  const [current, setCurrent] = useState(() => currentInstance().name);
  const instances = loadInstances();
  const cur = instances.find((i) => i.name === current) ?? instances[0];
  const id = instanceIdentity(cur);
  const pick = (n: string) => {
    setCurrentInstance(n);
    setCurrent(n);
    setOpen(false);
    onReconnect();
  };
  const nameStyle = (t: InstanceIdentity, color: string) => [
    styles.switchName,
    { color },
    (t.tone === "prod" || t.tone === "client") && styles.switchNameLoud,
    t.tone === "k8s" && styles.switchNameMedium,
  ];
  return (
    <View style={styles.switchWrap}>
      <Pressable style={[styles.switchBtn, { borderColor: id.color }]} onPress={() => setOpen((o) => !o)} accessibilityLabel="Switch environment">
        <Text style={styles.switchIcon}>{id.icon}</Text>
        {isMobile ? null : (
          <View style={styles.switchLabels}>
            <Text style={nameStyle(id, id.color)} numberOfLines={1}>{cur?.name ?? "Local"}</Text>
            <Text style={styles.switchKind} numberOfLines={1}>{id.kind}</Text>
          </View>
        )}
        <Text style={[styles.switchCaret, { color: id.color }]}>▾</Text>
      </Pressable>
      {open ? (
        <>
          <Pressable style={styles.switchScrim} onPress={() => setOpen(false)} />
          <View style={styles.switchMenu}>
            <Text style={styles.switchMenuLabel}>Environments</Text>
            {instances.map((i) => {
              const iid = instanceIdentity(i);
              const active = i.name === current;
              return (
                <Pressable key={i.name} style={({ hovered }: any) => [styles.switchItem, hovered && styles.navItemHover, active && styles.switchItemActive]} onPress={() => pick(i.name)}>
                  <View style={[styles.switchDot, { backgroundColor: iid.color }]} />
                  <Text style={styles.switchItemIcon}>{iid.icon}</Text>
                  <View style={{ flex: 1 }}>
                    <Text style={nameStyle(iid, iid.color)} numberOfLines={1}>{i.name}{active ? "  ✓" : ""}</Text>
                    <Text style={styles.switchItemSub} numberOfLines={1}>{iid.kind}{i.url ? "  ·  " + i.url.replace(/^https?:\/\//, "") : "  ·  same origin"}</Text>
                  </View>
                </Pressable>
              );
            })}
            <View style={styles.switchDivider} />
            <Pressable style={({ hovered }: any) => [styles.switchItem, hovered && styles.navItemHover]} onPress={() => { setOpen(false); onManage(); }}>
              <Text style={styles.switchItemIcon}>＋</Text>
              <Text style={styles.switchAdd}>Add / manage instances…</Text>
            </Pressable>
          </View>
        </>
      ) : null}
    </View>
  );
}

// ── breadcrumb toolbar ──────────────────────────────────────────────────────────
function Breadcrumb({
  nav,
  clientScreen,
  onNavigate,
}: {
  nav: NavTarget;
  clientScreen: ClientDestination | null;
  onNavigate: (t: NavTarget) => void;
}): ReactNode {
  const segs = nav.address.split("/").filter(Boolean);
  const styles = useSheet();
  return (
    <View style={styles.crumbs}>
      <Pressable style={({ hovered }: any) => [styles.crumbBtn, hovered && styles.crumbHover]} onPress={() => onNavigate(HOME)}>
        <Text style={styles.crumbHome}>⌂ Home</Text>
      </Pressable>
      {!clientScreen &&
        segs.map((seg, i) => {
          const address = segs.slice(0, i + 1).join("/");
          const last = i === segs.length - 1;
          return (
            <View key={address} style={styles.crumbSeg}>
              <Text style={styles.crumbSep}>›</Text>
              <Pressable style={({ hovered }: any) => [styles.crumbBtn, hovered && styles.crumbHover]} onPress={() => onNavigate({ address, area: CONTENT_AREA })}>
                <Text style={[styles.crumbText, last && styles.crumbTextLast]}>{seg}</Text>
              </Pressable>
            </View>
          );
        })}
      {nav.area !== CONTENT_AREA && !clientScreen ? <Text style={[styles.crumbSep, { marginLeft: 4 }]}>· {nav.area}</Text> : null}
    </View>
  );
}

// ── left menus (from providers) ─────────────────────────────────────────────────
function LeftMenu({
  tree,
  nav,
  clientScreen,
  onNavigate,
  onClientScreen,
}: {
  tree: AreaTree;
  nav: NavTarget;
  clientScreen: ClientDestination | null;
  onNavigate: (t: NavTarget) => void;
  onClientScreen: (d: ClientDestination | null) => void;
}): ReactNode {
  const styles = useSheet();
  return (
    <View style={styles.left}>
      <ScrollView contentContainerStyle={{ paddingVertical: 10 }}>
        <NavRow label="⌂  Home" active={!clientScreen && nav.address === HOME.address} onPress={() => onNavigate(HOME)} />

        {MESH_CONTEXTS.map((ctx) => {
          const items = menuItems(tree, ctx.key);
          if (items.length === 0) return null;
          return (
            <View key={ctx.key}>
              <Text style={styles.sectionLabel}>{ctx.glyph}  {ctx.label}</Text>
              {items.map((it, i) => (
                <NavRow
                  key={i}
                  label={it.label ?? it.area ?? ""}
                  active={!clientScreen && nav.area === it.area}
                  onPress={() => it.area && onNavigate({ address: nav.address, area: it.area })}
                />
              ))}
            </View>
          );
        })}

        {CLIENT_MENUS.map((ctx) => (
          <View key={ctx.context}>
            <Text style={styles.sectionLabel}>{ctx.glyph}  {ctx.context}</Text>
            {ctx.items.map((it) => (
              <NavRow
                key={it.destination}
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

function NavRow({ label, active, onPress }: { label: string; active: boolean; onPress: () => void }): ReactNode {
  const styles = useSheet();
  return (
    <Pressable style={({ hovered }: any) => [styles.navItem, hovered && styles.navItemHover, active && styles.navItemActive]} onPress={onPress}>
      <Text style={[styles.navItemText, active && styles.navItemTextActive]} numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

// ── main content ────────────────────────────────────────────────────────────────
function ContentPane({ source, nav, onNavigate }: { source: AreaSource; nav: NavTarget; onNavigate: (t: NavTarget) => void }): ReactNode {
  const styles = useSheet();
  const tree = useTree(source);
  const error = useAreaError(source);
  // The requested area faulted (access denied / node gone / transient) AND delivered no content:
  // show a classified notice instead of the silent blank column RenderArea would otherwise render.
  const showError = error != null && tree.areas?.[nav.area] == null;
  const onClickCapture = (e: any) => {
    const anchor = e?.target?.closest?.("a");
    if (!anchor) return;
    const t = anchor.getAttribute("href") ? parseHrefLocal(anchor.getAttribute("href"), nav.address) : null;
    if (t) {
      e.preventDefault();
      onNavigate(t);
    }
  };
  return (
    <View style={styles.content}>
      <ScrollView style={styles.contentScroll} contentContainerStyle={styles.contentInner}>
        <View style={styles.contentColumn} {...({ onClick: onClickCapture } as any)}>
          {showError ? <AreaErrorNotice message={error!} /> : <RenderArea areaKey={nav.area} />}
        </View>
      </ScrollView>
    </View>
  );
}

/** A friendly, classified notice for an area-subscription fault — the RN twin of the Blazor
 *  NamedAreaView placeholder: a denial / gone node gets a human message, never the raw
 *  framework diagnostic (a mobile client has no login/redirect flow, so it just informs). */
function AreaErrorNotice({ message }: { message: string }): ReactNode {
  const text = areaErrorMessage(message);
  return (
    <View
      accessibilityRole="alert"
      style={{ padding: 16, marginVertical: 16, borderRadius: 8, backgroundColor: "rgba(127,127,127,0.12)" }}
    >
      <Text style={{ fontSize: 15, lineHeight: 22 }}>{text}</Text>
    </View>
  );
}

// ── right sidebar ("On this page" TOC) ──────────────────────────────────────────
function RightSidebar({ contentKey }: { contentKey: string }): ReactNode {
  const styles = useSheet();
  const [toc, setToc] = useState<{ id: string; text: string; level: number }[]>([]);
  useEffect(() => {
    if (typeof document === "undefined") return;
    let raf = 0;
    const build = () => {
      const nodes = Array.from(document.querySelectorAll(".markdown-body h1, .markdown-body h2, .markdown-body h3")) as HTMLElement[];
      const items = nodes
        .filter((n) => (n.tagName === "H1" ? n.parentElement?.classList.contains("markdown-body") : true))
        .map((n) => ({ id: ensureId(n), text: (n.textContent ?? "").trim(), level: n.tagName === "H3" ? 3 : n.tagName === "H2" ? 2 : 1 }))
        .filter((i) => i.text.length > 0);
      setToc(items.length && items[0].level === 1 ? items.slice(1) : items);
    };
    setToc([]);
    const obs = new MutationObserver(() => { cancelAnimationFrame(raf); raf = requestAnimationFrame(build); });
    obs.observe(document.body, { childList: true, subtree: true });
    build();
    return () => { obs.disconnect(); cancelAnimationFrame(raf); };
  }, [contentKey]);

  return (
    <View style={styles.right}>
      <ScrollView contentContainerStyle={{ padding: 16 }}>
        <Text style={styles.sectionLabel}>On this page</Text>
        {toc.length === 0 ? (
          <Text style={styles.tocEmpty}>—</Text>
        ) : (
          toc.map((h, i) => (
            <Pressable key={i} style={({ hovered }: any) => [styles.tocItem, h.level === 3 && styles.tocItemSub, hovered && styles.navItemHover]} onPress={() => scrollToId(h.id)}>
              <Text style={[styles.tocText, h.level === 3 && styles.tocTextSub]} numberOfLines={2}>{h.text}</Text>
            </Pressable>
          ))
        )}
      </ScrollView>
    </View>
  );
}

// ── helpers ─────────────────────────────────────────────────────────────────────
function parseHrefLocal(href: string, currentAddress: string): NavTarget | null {
  if (!href || href.startsWith("#") || /^https?:\/\//i.test(href) || href.startsWith("mailto:")) return null;
  const raw = (href.startsWith("/") ? href : `${currentAddress}/${href}`).replace(/^\/+/, "");
  const parts: string[] = [];
  for (const seg of raw.split("/")) {
    if (seg === "..") parts.pop();
    else if (seg && seg !== ".") parts.push(seg);
  }
  return parts.length ? { address: parts.join("/"), area: CONTENT_AREA } : null;
}
function ensureId(el: HTMLElement): string {
  if (!el.id) el.id = (el.textContent ?? "").trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
  return el.id;
}
function scrollToId(id: string): void {
  if (typeof document !== "undefined") document.getElementById(id)?.scrollIntoView({ behavior: "smooth", block: "start" });
}

// ── styles (Outlook-for-macOS palette, themed) ───────────────────────────────────
const makeStyles = (p: Palette) => StyleSheet.create({
  root: { flex: 1, backgroundColor: p.appBg },
  // zIndex lifts the top bar's stacking context above the breadcrumb + body so the instance-switcher
  // dropdown (which overflows the bar) paints — and is clickable — on top of the content below it.
  topbar: { height: 52, flexDirection: "row", alignItems: "center", paddingHorizontal: 14, gap: 12, backgroundColor: p.topbarBg, borderBottomWidth: 1, borderBottomColor: p.border, zIndex: 30, ...({ position: "relative" } as any) },
  brand: { flexDirection: "row", alignItems: "center", gap: 8, width: 210 },
  brandMobile: { flexDirection: "row", alignItems: "center" },
  hamburger: { width: 34, height: 34, borderRadius: 8, alignItems: "center", justifyContent: "center" },
  hamburgerText: { fontSize: 20, color: p.text },
  scrim: { position: "absolute", top: 0, left: 0, right: 0, bottom: 0, backgroundColor: "rgba(0,0,0,0.35)", zIndex: 1 },
  drawer: { position: "absolute", top: 0, left: 0, bottom: 0, zIndex: 2, elevation: 8, shadowColor: "#000", shadowOpacity: 0.2, shadowRadius: 12, shadowOffset: { width: 2, height: 0 } },
  logo: { width: 26, height: 26, borderRadius: 6, backgroundColor: p.accent, alignItems: "center", justifyContent: "center" },
  logoMark: { color: p.onAccent, fontSize: 14, fontWeight: "700" },
  brandText: { fontSize: 15, fontWeight: "700", color: p.text },
  searchWrap: { flex: 1, maxWidth: 620, alignSelf: "center", flexDirection: "row", alignItems: "center", gap: 6, backgroundColor: p.surface, borderWidth: 1, borderColor: p.border, borderRadius: 8, paddingHorizontal: 10, height: 32 },
  searchIcon: { fontSize: 15, color: p.textMuted },
  search: { flex: 1, fontSize: 13.5, color: p.text, height: 30, ...({ outlineStyle: "none" } as any) },
  themeBtn: { width: 30, height: 30, borderRadius: 15, alignItems: "center", justifyContent: "center" },
  themeBtnHover: { backgroundColor: p.navHover },
  themeBtnText: { fontSize: 16, color: p.textSubtle },
  avatar: { width: 30, height: 30, borderRadius: 15, backgroundColor: "#5b5fc7", alignItems: "center", justifyContent: "center" },
  avatarText: { color: "#ffffff", fontSize: 13, fontWeight: "600" },

  // instance / environment switcher
  switchWrap: { position: "relative", zIndex: 50 },
  switchBtn: { flexDirection: "row", alignItems: "center", gap: 7, height: 34, paddingHorizontal: 10, borderRadius: 8, borderWidth: 1.5, backgroundColor: p.surface },
  switchIcon: { fontSize: 15 },
  switchLabels: { maxWidth: 150 },
  switchName: { fontSize: 13, fontWeight: "600" },
  switchNameLoud: { fontWeight: "800", textTransform: "uppercase", letterSpacing: 0.6, fontSize: 12 },
  switchNameMedium: { fontWeight: "700", letterSpacing: 0.3 },
  switchKind: { fontSize: 10, color: p.textMuted, marginTop: -1 },
  switchCaret: { fontSize: 11 },
  switchScrim: { position: "absolute", top: -100, left: -1000, right: -1000, height: 3000, ...({ cursor: "default" } as any) },
  switchMenu: { position: "absolute", top: 40, left: 0, minWidth: 300, backgroundColor: p.surface, borderWidth: 1, borderColor: p.border, borderRadius: 10, paddingVertical: 6, zIndex: 51, elevation: 12, shadowColor: "#000", shadowOpacity: 0.22, shadowRadius: 16, shadowOffset: { width: 0, height: 6 } },
  switchMenuLabel: { fontSize: 10, fontWeight: "700", color: p.textMuted, letterSpacing: 0.6, textTransform: "uppercase", paddingHorizontal: 14, paddingTop: 4, paddingBottom: 6 },
  switchItem: { flexDirection: "row", alignItems: "center", gap: 9, paddingHorizontal: 12, paddingVertical: 8, marginHorizontal: 5, borderRadius: 7 },
  switchItemActive: { backgroundColor: p.navActiveBg },
  switchDot: { width: 8, height: 8, borderRadius: 4 },
  switchItemIcon: { fontSize: 15, width: 20, textAlign: "center" },
  switchItemSub: { fontSize: 11, color: p.textMuted, marginTop: 1 },
  switchDivider: { height: 1, backgroundColor: p.border, marginVertical: 5, marginHorizontal: 10 },
  switchAdd: { fontSize: 13, color: p.accent, fontWeight: "600" },

  crumbs: { flexDirection: "row", alignItems: "center", height: 40, paddingHorizontal: 10, backgroundColor: p.surface, borderBottomWidth: 1, borderBottomColor: p.border, gap: 1 },
  crumbSeg: { flexDirection: "row", alignItems: "center", gap: 1 },
  crumbBtn: { paddingHorizontal: 8, paddingVertical: 5, borderRadius: 5 },
  crumbHover: { backgroundColor: p.navHover },
  crumbHome: { fontSize: 13, color: p.accent, fontWeight: "600" },
  crumbSep: { fontSize: 13, color: p.textMuted },
  crumbText: { fontSize: 13, color: p.textSubtle },
  crumbTextLast: { color: p.text, fontWeight: "600" },

  body: { flex: 1, flexDirection: "row", minHeight: 0 },
  left: { width: 236, flexGrow: 0, flexShrink: 0, backgroundColor: p.sidebarBg, borderRightWidth: 1, borderRightColor: p.border },
  sectionLabel: { fontSize: 11, fontWeight: "700", color: p.textMuted, letterSpacing: 0.5, textTransform: "uppercase", paddingHorizontal: 16, marginTop: 14, marginBottom: 6 },
  navItem: { paddingHorizontal: 16, paddingVertical: 7, marginHorizontal: 8, borderRadius: 6 },
  navItemHover: { backgroundColor: p.navHover },
  navItemActive: { backgroundColor: p.navActiveBg },
  navItemText: { fontSize: 13.5, color: p.text },
  navItemTextActive: { color: p.navActiveText, fontWeight: "600" },

  content: { flex: 1, minWidth: 0, backgroundColor: p.appBg },
  contentScroll: { flex: 1 },
  contentInner: { paddingHorizontal: 40, paddingVertical: 28, alignItems: "center" },
  contentColumn: { width: "100%", maxWidth: 900 },

  right: { width: 250, flexGrow: 0, flexShrink: 0, backgroundColor: p.rightBg, borderLeftWidth: 1, borderLeftColor: p.border },
  tocEmpty: { color: p.textMuted, fontSize: 13, paddingHorizontal: 16 },
  tocItem: { paddingVertical: 4, paddingHorizontal: 8, borderRadius: 5, marginBottom: 1 },
  tocItemSub: { paddingLeft: 20 },
  tocText: { fontSize: 12.5, color: p.textSubtle, lineHeight: 17 },
  tocTextSub: { color: p.textMuted, fontSize: 12 },
});
