// The live-ops leaves: ThreadChat, MeshSearch, MeshNodeCollection, Appearance and
// MeshNodeContentEditor — native ports of clients/react/src/controls/{threadChat,meshLive,
// appearance,meshNodeEditor}.tsx.
//
// These four were `livePlaceholder(...)` badges in rnPack ("▦ Thread chat"), left over from before
// the RN live wiring landed. The wiring HAS landed (liveOps.ts builds a full MeshOps: watch,
// startThread, submitMessage, patch, search), so the placeholders were rendering a dead label over
// a perfectly connected mesh. They run through the SAME optional MeshOps members the web pack uses,
// so a host that wires a MeshOpsProvider gets live behaviour and a host without one gets the empty
// state — no crash, no fake data.

import { useEffect, useMemo, useState } from "react";
import { View, Text, TextInput, Pressable, ScrollView, ActivityIndicator, Image, StyleSheet } from "react-native";
import { SvgUri, SvgXml } from "react-native-svg";
import {
  accessLogQuery,
  buildGroups,
  classifyIcon,
  mergeUnionResults,
  paintOrdered,
  parseScopeTabs,
  toAccessOrder,
  toSearchResult,
  unionQueries,
  useLocalize,
  useMeshOps,
  useResolve,
  str,
  useText,
  withRowOnlySelect,
  type ControlComponent,
  type MeshNodeState,
  type MeshOps,
  type MeshSearchResult,
  type MeshSearchScope,
} from "@meshweaver/react/core";
import { useNavigate } from "./nav";
import { resolveAssetUrl } from "./connection";
import { ComposerBar } from "./rnComposer";
import { useTheme } from "./theme";

const s = str;

// ── shared live hooks (the RN twins of the web pack's) ───────────────────────

/** Subscribe to a node's live state — the client twin of GetMeshNodeStream(path). */
export function useNodeState(ops: MeshOps | null, path: string | null): MeshNodeState | null {
  const [node, setNode] = useState<MeshNodeState | null>(null);
  useEffect(() => {
    if (!ops || !path) {
      setNode(null);
      return;
    }
    let live = true;
    void (async () => {
      try {
        for await (const n of ops.watch(path)) {
          if (!live) return;
          setNode(n);
        }
      } catch {
        // A dropped subscription leaves the last known state on screen rather than blanking it.
      }
    })();
    return () => {
      live = false;
    };
  }, [ops, path]);
  return node;
}

/** Watch each message cell of a thread — cells live at `{threadPath}/{id}`. */
function useMessageCells(ops: MeshOps | null, threadPath: string | null, ids: string[]): Record<string, ThreadMessageJson> {
  const [cells, setCells] = useState<Record<string, ThreadMessageJson>>({});
  const key = ids.join(",");
  useEffect(() => {
    if (!ops || !threadPath || ids.length === 0) return;
    let live = true;
    const stop: (() => void)[] = [];
    for (const id of ids) {
      const cellPath = `${threadPath}/${id}`;
      let running = true;
      stop.push(() => {
        running = false;
      });
      void (async () => {
        try {
          for await (const n of ops.watch(cellPath)) {
            if (!live || !running) return;
            setCells((c) => ({ ...c, [cellPath]: (n.content ?? {}) as ThreadMessageJson }));
          }
        } catch {
          /* a cell that never materialises falls back to its pending payload */
        }
      })();
    }
    return () => {
      live = false;
      for (const f of stop) f();
    };
    // `key` is the identity of the id LIST — re-subscribing per array reference would storm.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, threadPath, key]);
  return cells;
}

/** Debounced value — live-search keystrokes coalesce before hitting the mesh. */
function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

// A hidden query can be a newline-joined UNION of sub-queries (the server declares the home
// catalog that way; Blazor's MeshSearchView issues them as one MeshQueryRequest union, but the
// REST verb takes ONE query per call — a newline-joined string parses to nothing server-side).
// Run each line, concatenate in declaration order, dedupe by path — mergeUnionResults, the same
// shared-model half the web pack uses.
function useMeshQuery(ops: MeshOps | null, queries: string[], basePath?: string): { results: MeshSearchResult[]; loading: boolean } {
  const [state, setState] = useState<{ results: MeshSearchResult[]; loading: boolean }>({ results: [], loading: false });
  const key = queries.join("\n");
  useEffect(() => {
    if (!ops?.search || queries.length === 0) {
      setState({ results: [], loading: false });
      return;
    }
    let live = true;
    setState((st) => ({ ...st, loading: true }));
    Promise.all(queries.map((q) => ops.search!(q, basePath || undefined)))
      .then((batches) => {
        if (!live) return;
        setState({ results: mergeUnionResults(batches), loading: false });
      })
      .catch(() => {
        if (live) setState({ results: [], loading: false });
      });
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, key, basePath]);
  return state;
}

/**
 * The viewer's own access log as {activityId → accessed-at ms} — fetched only when a scope asked
 * for access ordering and the host exposes the viewer's id (ops.userId). Null until it lands, so
 * the tiles paint from their own query first and re-order when the log arrives.
 */
function useAccessOrder(ops: MeshOps | null, enabled: boolean): Map<string, number> | null {
  const viewer = enabled ? s(ops?.userId) : "";
  const [map, setMap] = useState<Map<string, number> | null>(null);
  useEffect(() => {
    if (!viewer || !ops?.search) {
      setMap(null);
      return;
    }
    let live = true;
    ops
      .search(accessLogQuery(viewer), undefined, 500)
      .then((rows) => {
        if (live) setMap(toAccessOrder(rows));
      })
      .catch(() => {
        /* keep the query's own order */
      });
    return () => {
      live = false;
    };
  }, [ops, viewer]);
  return map;
}

// ── ThreadChat ───────────────────────────────────────────────────────────────

interface ThreadMessageJson {
  role?: string;
  text?: string;
  authorName?: string;
  agentName?: string;
  status?: string;
}

interface ThreadJson {
  messages?: string[];
  pendingUserMessages?: Record<string, ThreadMessageJson>;
  composer?: { agentName?: string; modelName?: string; contextPath?: string };
  status?: string;
}

/**
 * A live thread: the ordered message bubbles plus a composer. Wire contract identical to the web
 * pack — `messages` is the ordered list of cell ids (each cell a node under the thread),
 * `pendingUserMessages` holds queued payloads the submission watcher has not drained yet, and
 * `status` drives the executing indicator.
 */
const ThreadChat: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const boundPath = s(useResolve(control.threadPath));
  const initialContext = s(useResolve(control.initialContext));
  const hideEmptyState = !!useResolve(control.hideEmptyState);

  // Once submit creates the thread, later sends drain through it — message 2+ must never
  // re-StartThread (the Blazor onCreated rule).
  const [createdPath, setCreatedPath] = useState<string | null>(null);
  const threadPath = createdPath ?? (boundPath || null);

  const threadNode = useNodeState(ops, threadPath);
  const thread = (threadNode?.content ?? {}) as ThreadJson;
  const isExecuting = thread.status === "StartingExecution" || thread.status === "Executing";

  const ids = useMemo(() => (Array.isArray(thread.messages) ? thread.messages.map(String) : []), [thread.messages]);
  const pending = (thread.pendingUserMessages ?? {}) as Record<string, ThreadMessageJson>;
  const cells = useMessageCells(ops, threadPath, ids);

  const items = [
    ...ids.map((id) => {
      const cell = threadPath ? cells[`${threadPath}/${id}`] : undefined;
      return { id, msg: cell ?? pending[id] };
    }),
    ...Object.keys(pending)
      .filter((id) => !ids.includes(id))
      .map((id) => ({ id, msg: pending[id] })),
  ].filter((x): x is { id: string; msg: ThreadMessageJson } => x.msg != null);

  // The namespace a NEW thread is created under (ThreadChatControl.namespacePath), resolved with the
  // other bindings — never inside the submit handler.
  const namespacePath = s(useResolve(control.namespacePath ?? control.namespace)) || initialContext;

  return (
    <View style={styles.chat}>
      <ScrollView style={styles.chatLog} contentContainerStyle={{ gap: 8 }}>
        {items.length === 0 && !hideEmptyState ? (
          <Text style={styles.muted}>{t("chat.startConversation")}</Text>
        ) : null}
        {items.map(({ id, msg }) => {
          const mine = /user/i.test(s(msg.role) || "user");
          return (
            <View key={id} style={{ flexDirection: "row", justifyContent: mine ? "flex-end" : "flex-start" }}>
              <View style={[styles.bubble, mine ? styles.bubbleMine : styles.bubbleTheirs]}>
                <Text style={styles.body}>{s(msg.text)}</Text>
              </View>
            </View>
          );
        })}
        {isExecuting ? (
          <View style={styles.executingRow}>
            <ActivityIndicator />
            <Text style={styles.muted}>{t("chat.working")}</Text>
          </View>
        ) : null}
      </ScrollView>
      {/* ONE composer everywhere: the shared model (core useMentionModel) + the speech pipeline,
          rendered by ComposerBar — the same surface the deleted app-level bar used to be. */}
      <ComposerBar
        ops={ops}
        threadPath={threadPath}
        namespacePath={namespacePath || undefined}
        contextPath={initialContext || undefined}
        onThreadStarted={setCreatedPath}
      />
    </View>
  );
};

// ── MeshSearch ───────────────────────────────────────────────────────────────

/** The tile/row icon: the node's Icon column (SVG / URL / emoji) or the initial bubble — the
 *  native mapping of the web pack's NodeResultIcon fallback chain. */
function ResultIcon({ node, size, radius }: { node: MeshSearchResult; size: number; radius: number }) {
  const value = node.icon || node.thumbnail || "";
  const classified = classifyIcon(value as never);
  const box = { width: size, height: size, borderRadius: radius, overflow: "hidden" as const, alignItems: "center" as const, justifyContent: "center" as const };
  switch (classified.kind) {
    case "svg":
      return (
        <View style={box}>
          <SvgXml xml={classified.text} width={Math.round(size * 0.6)} height={Math.round(size * 0.6)} />
        </View>
      );
    case "url": {
      // Two native traps the web pack never sees: a mesh-RELATIVE url ("/static/…") has no origin
      // to resolve against on a device (RN Image silently renders nothing), and RN's Image cannot
      // decode SVG — which is what nearly every node icon is. Resolve against the current instance
      // and route .svg through react-native-svg, exactly as the doc renderer's <img> leaf does.
      const url = resolveAssetUrl(classified.text);
      if (/\.svg(\?|#|$)/i.test(url))
        return (
          <View style={box}>
            <SvgUri uri={url} width={Math.round(size * 0.6)} height={Math.round(size * 0.6)} />
          </View>
        );
      return <Image source={{ uri: url }} style={{ ...box, resizeMode: "cover" }} />;
    }
    case "emoji":
      return (
        <View style={box}>
          <Text style={{ fontSize: Math.round(size * 0.55), lineHeight: Math.round(size * 0.7) }}>{classified.text}</Text>
        </View>
      );
    default:
      return (
        <View style={[box, styles.iconInitialBox]}>
          <Text style={{ fontSize: Math.round(size * 0.4), fontWeight: "600", color: "#616161" }}>
            {(node.name.trim()[0] ?? "?").toUpperCase()}
          </Text>
        </View>
      );
  }
}

/** One tile of the phone-home ICON grid (Icons render mode): a large rounded icon with the name
 *  underneath, navigating to the row's TARGET — painted entirely from the query row. */
function IconTile({ node, target }: { node: MeshSearchResult; target: string }) {
  const navigate = useNavigate();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={node.name}
      style={styles.iconTile}
      onPress={() => navigate({ address: target, area: "" })}
    >
      <View style={styles.iconBox}>
        <ResultIcon node={node} size={64} radius={16} />
      </View>
      <Text style={styles.iconLabel} numberOfLines={2}>
        {node.name}
      </Text>
    </Pressable>
  );
}

/**
 * Live mesh search — the native twin of the web pack's MeshSearchView, over the SAME shared model
 * (@meshweaver/react/core meshSearchModel): scope tabs (a strip only for 2+ tabs; one tab still
 * applies its settings), newline-joined UNION queries issued per line, the Icons phone-home grid
 * painted from query rows (row-only select), NavigateToMainNode, most-recently-used-first
 * ordering from the viewer's own access log (SortByAccess), and grouped-by-type sections with
 * counts, biggest group first (GroupByFrequency).
 */
const MeshSearch: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const navigate = useNavigate();
  const title = useText(control.title);
  const controlHiddenQuery = s(useResolve(control.hiddenQuery));
  const initialVisible = s(useResolve(control.visibleQuery));
  const placeholder = s(useResolve(control.placeholder)) || t("common.typeToSearch");
  const ns = s(useResolve(control.namespace));
  const controlRenderMode = s(useResolve(control.renderMode)) || "Flat";
  const showSearchBox = useResolve(control.showSearchBox) !== false;
  const liveSearch = useResolve(control.liveSearch) !== false;
  const excludeBasePath = useResolve(control.excludeBasePath) !== false;
  const showEmptyMessage = useResolve(control.showEmptyMessage) !== false;
  const controlNavigateToMainNode = useResolve(control.navigateToMainNode) === true;
  const groupByFrequency = useResolve(control.groupByFrequency) === true;
  const sections = (control.sections ?? {}) as Record<string, unknown>;
  const showCounts = sections.showCounts !== false;
  const grouping = (control.grouping ?? {}) as Record<string, unknown>;

  // Scope tabs — active tab tracked by LABEL (a bare index could re-point on a list change);
  // a vanished label clamps to the first tab. A single tab renders no strip but still applies
  // its settings (the home's Apps band: Icons + SortByAccess on one scope).
  const scopeTabs = useMemo(() => parseScopeTabs(control.scopeTabs), [control.scopeTabs]);
  const [activeScopeLabel, setActiveScopeLabel] = useState<string | null>(null);
  const activeScopeIndex = Math.max(
    0,
    scopeTabs.findIndex((sc) => sc.label === activeScopeLabel),
  );
  const scope: MeshSearchScope | null = scopeTabs[activeScopeIndex] ?? null;

  const hiddenQuery = scope ? scope.query : controlHiddenQuery;
  const renderMode = scope?.renderMode ?? controlRenderMode;
  const navigateToMainNode = scope?.navigateToMainNode ?? controlNavigateToMainNode;
  const sortByAccess = scope?.sortByAccess ?? false;
  const isIcons = renderMode === "Icons";
  const isGrouped = renderMode === "Grouped";

  const [visible, setVisible] = useState(initialVisible);
  const [submitted, setSubmitted] = useState(initialVisible);
  const term = useDebounced(liveSearch ? visible : submitted, 250);
  // Each UNION leg gets the visible term appended; the icon grid ships row-only (`select:`
  // without content) unless the authored query already selects.
  const queries = unionQueries(hiddenQuery, term, (leg) => withRowOnlySelect(leg, isIcons));
  const { results, loading } = useMeshQuery(ops, queries, ns);

  const accessOrder = useAccessOrder(ops, sortByAccess);
  const targetOf = (n: MeshSearchResult) => (navigateToMainNode && n.mainNode ? n.mainNode : n.path);
  let items = excludeBasePath && ns ? results.filter((n) => n.path !== ns) : results;
  if (sortByAccess) items = paintOrdered(items, accessOrder, targetOf);

  const groupBy = s(grouping.groupByProperty) || "NodeType";
  const groups = buildGroups(items, isGrouped, groupBy, groupByFrequency);
  const skipHeaders = groups.length === 1;
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());
  const toggleCollapsed = (key: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const renderItems = (groupItems: MeshSearchResult[]) =>
    isIcons ? (
      <View style={styles.iconsGrid}>
        {groupItems.map((n) => (
          <IconTile key={n.path} node={n} target={targetOf(n)} />
        ))}
      </View>
    ) : (
      <View style={{ gap: 8 }}>
        {groupItems.map((n) => (
          <Pressable key={n.path} style={styles.resultRow} onPress={() => navigate({ address: targetOf(n), area: "" })}>
            <View style={{ flexDirection: "row", alignItems: "center", gap: 8 }}>
              <ResultIcon node={n} size={28} radius={6} />
              <Text style={styles.resultName}>{n.name}</Text>
            </View>
            {n.description ? <Text style={styles.muted}>{n.description}</Text> : null}
            <Text style={styles.resultPath}>{n.path}</Text>
          </Pressable>
        ))}
      </View>
    );

  return (
    <View style={{ gap: 8 }}>
      {scopeTabs.length > 1 ? (
        <View style={styles.scopeRow} accessibilityRole="tablist">
          {scopeTabs.map((tab, i) => (
            <Pressable
              key={tab.label || String(i)}
              accessibilityRole="tab"
              accessibilityState={{ selected: i === activeScopeIndex }}
              style={[styles.scopeTab, i === activeScopeIndex && styles.scopeTabActive]}
              onPress={() => {
                setActiveScopeLabel(tab.label);
                setCollapsed(new Set());
              }}
            >
              <Text style={[styles.scopeTabText, i === activeScopeIndex && styles.scopeTabTextActive]}>{tab.label}</Text>
            </Pressable>
          ))}
        </View>
      ) : null}
      {title ? <Text style={styles.sectionTitle}>{title}</Text> : null}
      {showSearchBox ? (
        <View style={styles.searchRow}>
          <Text style={styles.searchIcon}>🔍</Text>
          <TextInput
            style={styles.searchInput}
            value={visible}
            onChangeText={setVisible}
            onSubmitEditing={() => setSubmitted(visible)}
            placeholder={placeholder}
            autoCapitalize="none"
          />
        </View>
      ) : null}
      {loading ? <ActivityIndicator /> : null}
      {!loading && items.length === 0 && showEmptyMessage ? <Text style={styles.muted}>{t("common.noResults")}</Text> : null}
      {groups.map((group) => {
        const isCollapsed = collapsed.has(group.key);
        const headerLabel = showCounts ? `${group.label} (${group.items.length})` : group.label;
        return (
          <View key={group.key || "·"} style={{ gap: 6 }}>
            {!skipHeaders ? (
              <Pressable style={styles.groupHeader} onPress={() => toggleCollapsed(group.key)}>
                <Text style={styles.groupChevron}>{isCollapsed ? "▶" : "▼"}</Text>
                <Text style={styles.groupLabel}>{headerLabel}</Text>
              </Pressable>
            ) : null}
            {!isCollapsed ? renderItems(group.items) : null}
          </View>
        );
      })}
    </View>
  );
};

// ── MeshNodeCollection ───────────────────────────────────────────────────────

/** A node collection: one or more server-declared queries, each rendered as a list of nodes. */
const MeshNodeCollection: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const navigate = useNavigate();
  const queries = (Array.isArray(control.queries) ? control.queries : []).map(s).filter(Boolean);
  const [items, setItems] = useState<MeshSearchResult[] | null>(null);

  // JSON.stringify: an unambiguous identity for the query LIST — a bare join can collide
  // (["a","bc"] vs ["ab","c"]) and leave stale results on a list change.
  const key = JSON.stringify(queries);
  useEffect(() => {
    if (!ops?.search || queries.length === 0) {
      setItems([]);
      return;
    }
    let live = true;
    Promise.all(queries.map((q) => ops.search!(q)))
      .then((batches) => {
        if (!live) return;
        setItems(mergeUnionResults(batches));
      })
      .catch(() => live && setItems([]));
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, key]);

  if (items == null) return <ActivityIndicator />;
  if (items.length === 0) return <Text style={styles.muted}>{t("empty.nothingYet")}</Text>;
  return (
    <View style={{ gap: 8 }}>
      {items.map((n) => (
        <Pressable key={n.path} style={styles.resultRow} onPress={() => navigate({ address: n.path, area: "" })}>
          <Text style={styles.resultName}>{n.name}</Text>
          {n.description ? <Text style={styles.muted}>{n.description}</Text> : null}
        </Pressable>
      ))}
    </View>
  );
};

// ── Appearance ───────────────────────────────────────────────────────────────

/**
 * The theme settings panel — the native mirror of Blazor's AppearanceView, bound to the SAME
 * ThemeProvider the RN shell installs (which persists to localStorage["mw-theme"] on Expo web).
 * Accent colour / text direction (Blazor's OfficeColor + Direction) are not ported, matching the
 * web pack.
 */
const Appearance: ControlComponent = () => {
  const t = useLocalize();
  const { mode, toggle } = useTheme();
  return (
    <View style={{ gap: 12, maxWidth: 500 }}>
      <Text style={styles.sectionTitle}>{t("appearance.theme")}</Text>
      <View style={styles.modeRow}>
        {(["light", "dark"] as const).map((m) => (
          <Pressable
            key={m}
            accessibilityRole="radio"
            accessibilityState={{ selected: mode === m }}
            style={[styles.modeChip, mode === m && styles.modeChipOn]}
            onPress={() => {
              if (mode !== m) toggle();
            }}
          >
            <Text style={[styles.modeChipText, mode === m && styles.modeChipTextOn]}>
              {m === "light" ? t("appearance.light") : t("appearance.dark")}
            </Text>
          </Pressable>
        ))}
      </View>
      <Text style={styles.muted}>
        {t("appearance.persistedOnDevice")}
      </Text>
    </View>
  );
};

// ── MeshNodeContentEditor ────────────────────────────────────────────────────

interface EditorField {
  name: string;
  label: string;
  kind: "text" | "boolean" | "number";
}

/**
 * A node-bound content editor: reads the node's live content off `ops.watch` and writes each field
 * back with a field-level `ops.patch` — the client twin of
 * `GetMeshNodeStream(path).Update(...)`, and the one editing surface AGENTS.md sanctions (no
 * replicate-into-/data-then-save copy).
 */
const MeshNodeContentEditor: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const nodePath = s(useResolve(control.nodePath ?? control.path));
  const node = useNodeState(ops, nodePath || null);
  const content = (node?.content ?? {}) as Record<string, unknown>;

  const fields: EditorField[] = useMemo(() => {
    const declared = Array.isArray(control.fields) ? (control.fields as Record<string, unknown>[]) : [];
    if (declared.length > 0)
      return declared.map((f) => ({
        name: s(f.name ?? f.property),
        label: s(f.label ?? f.title) || s(f.name ?? f.property),
        kind: (s(f.kind ?? f.type).toLowerCase() as EditorField["kind"]) || "text",
      }));
    // No declared field list → edit the node's own scalar content fields, which is what
    // MeshNodeContentEditorControl.ForType produces server-side.
    return Object.keys(content)
      .filter((k) => ["string", "number", "boolean"].includes(typeof content[k]))
      .map((k) => ({
        name: k,
        label: k.replace(/([A-Z])/g, " $1").replace(/^./, (c) => c.toUpperCase()),
        kind: typeof content[k] === "boolean" ? "boolean" : typeof content[k] === "number" ? "number" : "text",
      }));
  }, [control.fields, content]);

  if (!nodePath) return <Text style={styles.muted}>{t("editor.noNodeBound")}</Text>;
  if (!ops) return <Text style={styles.muted}>{t("unavailable.editing")}</Text>;

  const write = (name: string, value: unknown) => ops.patch(nodePath, { content: { [name]: value } });

  return (
    <View style={{ gap: 12 }}>
      {fields.map((f) => (
        <View key={f.name} style={{ gap: 4 }}>
          <Text style={styles.propertyLabel}>{f.label}</Text>
          {f.kind === "boolean" ? (
            <Pressable
              accessibilityRole="checkbox"
              accessibilityState={{ checked: !!content[f.name] }}
              style={styles.checkRow}
              onPress={() => write(f.name, !content[f.name])}
            >
              <Text style={styles.checkGlyph}>{content[f.name] ? "☑" : "☐"}</Text>
              <Text style={styles.body}>{f.label}</Text>
            </Pressable>
          ) : (
            <TextInput
              style={styles.input}
              value={s(content[f.name])}
              keyboardType={f.kind === "number" ? "numeric" : "default"}
              onChangeText={(t) => write(f.name, f.kind === "number" ? Number(t) : t)}
            />
          )}
        </View>
      ))}
    </View>
  );
};

export const rnLiveControls: Record<string, ControlComponent> = {
  ThreadChat,
  MeshSearch,
  MeshNodeCollection,
  Appearance,
  MeshNodeContentEditor,
};

const styles = StyleSheet.create({
  body: { fontSize: 14, color: "#242424" },
  muted: { fontSize: 12, color: "#616161" },
  error: { fontSize: 12, color: "#a4262c" },
  sectionTitle: { fontSize: 16, fontWeight: "700", color: "#242424" },
  input: { borderWidth: 1, borderColor: "#ccc", borderRadius: 4, padding: 8, fontSize: 14 },
  propertyLabel: { fontSize: 13, fontWeight: "600", color: "#242424" },
  // chat
  chat: { gap: 8, flex: 1, minHeight: 0 },
  chatLog: { flexGrow: 0 },
  bubble: { maxWidth: "78%", paddingVertical: 8, paddingHorizontal: 12, borderRadius: 12 },
  bubbleMine: { backgroundColor: "#cfe4fa" },
  bubbleTheirs: { backgroundColor: "#f0f0f0" },
  executingRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  composerRow: { flexDirection: "row", alignItems: "flex-end", gap: 8 },
  composerInput: { flex: 1, borderWidth: 1, borderColor: "#ccc", borderRadius: 6, padding: 8, fontSize: 14, maxHeight: 120 },
  sendButton: { backgroundColor: "#0f6cbd", width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center" },
  sendButtonDisabled: { backgroundColor: "#a0a0a0" },
  sendButtonText: { color: "white", fontWeight: "600" },
  // search / collection
  searchRow: { flexDirection: "row", alignItems: "center", gap: 6, borderWidth: 1, borderColor: "#ccc", borderRadius: 6, paddingHorizontal: 8, backgroundColor: "white" },
  searchIcon: { fontSize: 14 },
  searchInput: { flex: 1, paddingVertical: 8, fontSize: 14 },
  resultRow: { gap: 2, padding: 10, borderWidth: 1, borderColor: "#e1e1e1", borderRadius: 6, backgroundColor: "white" },
  resultName: { fontSize: 14, fontWeight: "600", color: "#242424" },
  resultPath: { fontSize: 11, color: "#8a8886" },
  // scope tabs (the home's shared search bar across scopes)
  scopeRow: { flexDirection: "row", gap: 4, borderBottomWidth: 1, borderBottomColor: "#d1d1d1" },
  scopeTab: { paddingVertical: 8, paddingHorizontal: 12, borderBottomWidth: 2, borderBottomColor: "transparent" },
  scopeTabActive: { borderBottomColor: "#0f6cbd" },
  scopeTabText: { fontSize: 14, color: "#242424" },
  scopeTabTextActive: { color: "#0f6cbd", fontWeight: "600" },
  // the phone-home icon grid (Icons render mode)
  iconsGrid: { flexDirection: "row", flexWrap: "wrap", gap: 8, paddingVertical: 8 },
  iconTile: { width: 88, alignItems: "center", gap: 6, padding: 4, borderRadius: 12 },
  iconBox: { width: 64, height: 64, borderRadius: 16, alignItems: "center", justifyContent: "center", backgroundColor: "#f0f0f0", borderWidth: 1, borderColor: "#e1e1e1", overflow: "hidden" },
  iconLabel: { fontSize: 12, lineHeight: 15, textAlign: "center", color: "#242424" },
  iconInitialBox: { backgroundColor: "#f0f0f0" },
  // grouped sections (the home's content fan-out by type)
  groupHeader: { flexDirection: "row", alignItems: "center", gap: 6, paddingVertical: 4 },
  groupChevron: { fontSize: 10, color: "#616161" },
  groupLabel: { fontSize: 14, fontWeight: "600", color: "#242424" },
  // appearance
  modeRow: { flexDirection: "row", gap: 8 },
  modeChip: { paddingVertical: 8, paddingHorizontal: 14, borderRadius: 16, borderWidth: 1, borderColor: "#ccc" },
  modeChipOn: { backgroundColor: "#0f6cbd", borderColor: "#0f6cbd" },
  modeChipText: { fontSize: 13, color: "#242424" },
  modeChipTextOn: { color: "white", fontWeight: "600" },
  // editor
  checkRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  checkGlyph: { fontSize: 18, color: "#0f6cbd" },
});
