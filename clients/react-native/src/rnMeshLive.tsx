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
import { View, Text, TextInput, Pressable, ScrollView, ActivityIndicator, StyleSheet } from "react-native";
import {
  useLocalize,
  useMeshOps,
  useResolve,
  str,
  useText,
  type ControlComponent,
  type MeshNodeState,
  type MeshOps,
} from "@meshweaver/react/core";
import { useNavigate } from "./nav";
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

interface NodeResult {
  path: string;
  name: string;
  nodeType: string;
  description: string;
}

function toNodeResult(r: Record<string, unknown>): NodeResult {
  const content = (r.content ?? {}) as Record<string, unknown>;
  return {
    path: s(r.path),
    name: s(r.name) || s(r.path).split("/").pop() || s(r.path),
    nodeType: s(r.nodeType),
    description: s(r.description ?? content.description),
  };
}

function useMeshQuery(ops: MeshOps | null, query: string, basePath?: string): { results: NodeResult[]; loading: boolean } {
  const [state, setState] = useState<{ results: NodeResult[]; loading: boolean }>({ results: [], loading: false });
  useEffect(() => {
    if (!ops?.search || !query) {
      setState({ results: [], loading: false });
      return;
    }
    let live = true;
    setState((st) => ({ ...st, loading: true }));
    ops
      .search(query, basePath || undefined)
      .then((rs) => {
        if (!live) return;
        setState({ results: rs.map(toNodeResult).filter((n) => n.path.length > 0), loading: false });
      })
      .catch(() => {
        if (live) setState({ results: [], loading: false });
      });
    return () => {
      live = false;
    };
  }, [ops, query, basePath]);
  return state;
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

  const [text, setText] = useState("");
  const [sendError, setSendError] = useState<string | null>(null);
  const [sending, setSending] = useState(false);

  // The namespace a NEW thread is created under (ThreadChatControl.namespacePath), resolved with the
  // other bindings — never inside the submit handler.
  const namespacePath = s(useResolve(control.namespacePath ?? control.namespace)) || initialContext;

  const send = () => {
    const body = text.trim();
    if (!body || !ops || sending) return;
    setSending(true);
    setSendError(null);
    const done = () => {
      setSending(false);
      setText("");
    };
    const fail = (e: unknown) => {
      setSending(false);
      setSendError(e instanceof Error ? e.message : String(e));
    };
    if (threadPath) {
      ops.submitMessage(threadPath, body, { contextPath: initialContext || undefined }).then(done, fail);
    } else {
      if (!namespacePath) {
        setSending(false);
        setSendError(t("chat.noNamespace"));
        return;
      }
      ops.startThread(namespacePath, body, { contextPath: initialContext || undefined }).then((r) => {
        setCreatedPath(r.path);
        done();
      }, fail);
    }
  };

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
      {sendError ? <Text style={styles.error}>{sendError}</Text> : null}
      <View style={styles.composerRow}>
        <TextInput
          style={styles.composerInput}
          value={text}
          onChangeText={setText}
          placeholder={t("chat.composerPlaceholder")}
          multiline
          editable={!!ops}
          onSubmitEditing={send}
        />
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("common.send")}
          style={[styles.sendButton, (!text.trim() || sending) && styles.sendButtonDisabled]}
          onPress={send}
        >
          <Text style={styles.sendButtonText}>{sending ? "…" : t("common.send")}</Text>
        </Pressable>
      </View>
    </View>
  );
};

// ── MeshSearch ───────────────────────────────────────────────────────────────

/**
 * Live mesh search: the hidden query (the control's server-declared filter) is combined with the
 * user's visible term and run through `ops.search`, exactly as the web pack does.
 */
const MeshSearch: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const navigate = useNavigate();
  const title = useText(control.title);
  const hiddenQuery = s(useResolve(control.hiddenQuery));
  const initialVisible = s(useResolve(control.visibleQuery));
  const placeholder = s(useResolve(control.placeholder)) || t("common.typeToSearch");
  const ns = s(useResolve(control.namespace));
  const showSearchBox = useResolve(control.showSearchBox) !== false;
  const liveSearch = useResolve(control.liveSearch) !== false;
  const excludeBasePath = useResolve(control.excludeBasePath) !== false;
  const showEmptyMessage = useResolve(control.showEmptyMessage) !== false;

  const [visible, setVisible] = useState(initialVisible);
  const [submitted, setSubmitted] = useState(initialVisible);
  const term = useDebounced(liveSearch ? visible : submitted, 250);
  const query = [hiddenQuery, term].map((t) => t.trim()).filter(Boolean).join(" ");
  const { results, loading } = useMeshQuery(ops, query, ns);
  const items = excludeBasePath && ns ? results.filter((n) => n.path !== ns) : results;

  return (
    <View style={{ gap: 8 }}>
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
      {items.map((n) => (
        <Pressable key={n.path} style={styles.resultRow} onPress={() => navigate({ address: n.path, area: "Overview" })}>
          <Text style={styles.resultName}>{n.name}</Text>
          {n.description ? <Text style={styles.muted}>{n.description}</Text> : null}
          <Text style={styles.resultPath}>{n.path}</Text>
        </Pressable>
      ))}
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
  const [items, setItems] = useState<NodeResult[] | null>(null);

  const key = queries.join("|");
  useEffect(() => {
    if (!ops?.search || queries.length === 0) {
      setItems([]);
      return;
    }
    let live = true;
    Promise.all(queries.map((q) => ops.search!(q)))
      .then((batches) => {
        if (!live) return;
        const seen = new Set<string>();
        const flat: NodeResult[] = [];
        for (const rows of batches)
          for (const r of rows) {
            const n = toNodeResult(r);
            if (n.path && !seen.has(n.path)) {
              seen.add(n.path);
              flat.push(n);
            }
          }
        setItems(flat);
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
        <Pressable key={n.path} style={styles.resultRow} onPress={() => navigate({ address: n.path, area: "Overview" })}>
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
  sendButton: { backgroundColor: "#0f6cbd", paddingVertical: 10, paddingHorizontal: 14, borderRadius: 6 },
  sendButtonDisabled: { backgroundColor: "#a0a0a0" },
  sendButtonText: { color: "white", fontWeight: "600" },
  // search / collection
  searchRow: { flexDirection: "row", alignItems: "center", gap: 6, borderWidth: 1, borderColor: "#ccc", borderRadius: 6, paddingHorizontal: 8, backgroundColor: "white" },
  searchIcon: { fontSize: 14 },
  searchInput: { flex: 1, paddingVertical: 8, fontSize: 14 },
  resultRow: { gap: 2, padding: 10, borderWidth: 1, borderColor: "#e1e1e1", borderRadius: 6, backgroundColor: "white" },
  resultName: { fontSize: 14, fontWeight: "600", color: "#242424" },
  resultPath: { fontSize: 11, color: "#8a8886" },
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
