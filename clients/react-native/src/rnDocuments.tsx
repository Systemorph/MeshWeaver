// Document + node-transfer + file-browser leaves: DocumentSource, ExportDocument, NodeExport,
// NodeImport, FileBrowser — native ports of clients/react/src/controls/{documentControls,
// nodeTransfer,fileBrowser}.tsx.
//
// All five run on the SAME optional MeshOps members the web pack uses (listContent / uploadContent /
// exportDocument / getNode / createNode), which liveOps.ts now wires for RN too. A host without a
// given op gets the same explicit "not available in this frontend" notice the web pack renders —
// never a silent blank.

import { useEffect, useMemo, useState } from "react";
import { View, Text, TextInput, Pressable, ScrollView, ActivityIndicator, StyleSheet, Linking } from "react-native";
import {
  useLocalize,
  useMeshOps,
  useResolve,
  str,
  useText,
  type ContentListing,
  type ControlComponent,
} from "@meshweaver/react/core";

const s = str;

/** An explicit "this host does not wire that op" notice — the RN twin of the web pack's MessageBar. */
function Notice({ textKey }: { textKey: string }) {
  const t = useLocalize();
  return (
    <View style={styles.notice}>
      <Text style={styles.noticeText}>{t(textKey)}</Text>
    </View>
  );
}

function safeName(url: string): string {
  const tail = url.split("?")[0].split("/").pop() ?? "";
  return tail || "document";
}

function safeSlug(path: string): string {
  return (path.split("/").pop() || "export").replace(/[^\w.-]+/g, "-");
}

// ── DocumentSource ───────────────────────────────────────────────────────────
/**
 * A source document attached to a node. The web view inlines a PDF in an <iframe>; RN has no
 * iframe and no built-in PDF surface, so the document opens in the system viewer through Linking
 * behind a file card that carries the name, type and highlight — the native idiom for "open this
 * attachment".
 */
const DocumentSource: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const fileUrl = useText(control.fileUrl);
  const mime = useText(control.mime);
  const highlight = useText(control.highlight);
  const fileName = useText(control.fileName) || safeName(fileUrl);
  if (!fileUrl) return null;
  const isPdf = /pdf/i.test(mime) || /\.pdf(\?|$)/i.test(fileUrl);
  return (
    <Pressable
      accessibilityRole="link"
      style={styles.fileCard}
      onPress={() => void Linking.openURL(fileUrl).catch(() => undefined)}
    >
      <Text style={styles.fileGlyph}>{isPdf ? "📕" : "📄"}</Text>
      <View style={{ flex: 1 }}>
        <Text style={styles.fileName}>{fileName}</Text>
        {mime ? <Text style={styles.muted}>{mime}</Text> : null}
        {highlight ? <Text style={styles.highlight}>{highlight}</Text> : null}
      </View>
      <Text style={styles.muted}>{t("ui.open")}</Text>
    </Pressable>
  );
};

// ── ExportDocument ───────────────────────────────────────────────────────────
type ExportState =
  | { status: "idle" | "exporting" }
  | { status: "done"; fileName: string; size: number }
  | { status: "error"; message: string };

/**
 * Render a node (optionally with its descendants) to PDF/DOCX through the mesh export Activity.
 *
 * The web pack finishes by triggering a browser download. RN has no download surface without a
 * file-system dependency, so a finished export reports the produced document and its size; the
 * bytes are already in hand for a host that wants to share them.
 */
const ExportDocument: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const sourcePath = useText(control.sourcePath);
  const nodeName = useText(control.nodeName);
  const hasDescendants = !!useResolve(control.hasDescendants);
  const defaultFormat = (s(useResolve(control.defaultFormat)) || "pdf").toLowerCase() === "docx" ? "docx" : "pdf";

  const [format, setFormat] = useState<"pdf" | "docx">(defaultFormat as "pdf" | "docx");
  const [includeChildren, setIncludeChildren] = useState(false);
  const [state, setState] = useState<ExportState>({ status: "idle" });

  if (!ops?.exportDocument) return <Notice textKey="unavailable.documentExport" />;

  const run = () => {
    if (!sourcePath || state.status === "exporting") return;
    setState({ status: "exporting" });
    ops
      .exportDocument!(sourcePath, { format, title: nodeName || undefined, includeChildren })
      .then((d) => setState({ status: "done", fileName: d.fileName, size: d.bytes.length }))
      .catch((e) => setState({ status: "error", message: e instanceof Error ? e.message : String(e) }));
  };

  return (
    <View style={{ gap: 10 }}>
      <View style={styles.chipRow}>
        {(["pdf", "docx"] as const).map((f) => (
          <Pressable
            key={f}
            accessibilityRole="radio"
            accessibilityState={{ selected: format === f }}
            style={[styles.chip, format === f && styles.chipOn]}
            onPress={() => setFormat(f)}
          >
            <Text style={[styles.chipText, format === f && styles.chipTextOn]}>{f.toUpperCase()}</Text>
          </Pressable>
        ))}
      </View>
      {hasDescendants ? (
        <Pressable
          accessibilityRole="checkbox"
          accessibilityState={{ checked: includeChildren }}
          style={styles.checkRow}
          onPress={() => setIncludeChildren((v) => !v)}
        >
          <Text style={styles.checkGlyph}>{includeChildren ? "☑" : "☐"}</Text>
          <Text style={styles.body}>{t("export.includeChildren")}</Text>
        </Pressable>
      ) : null}
      <Pressable accessibilityRole="button" style={styles.button} onPress={run}>
        <Text style={styles.buttonText}>{state.status === "exporting" ? `${t("menu.export")}…` : t("menu.export")}</Text>
      </Pressable>
      {state.status === "exporting" ? <ActivityIndicator /> : null}
      {state.status === "done" ? (
        <Text style={styles.success}>
          {state.fileName} ({Math.max(1, Math.round(state.size / 1024))} KB)
        </Text>
      ) : null}
      {state.status === "error" ? <Text style={styles.error}>{state.message}</Text> : null}
    </View>
  );
};

// ── NodeExport / NodeImport ──────────────────────────────────────────────────
interface MeshBundle {
  meshExport: number;
  root: string;
  exportedAt?: string;
  nodes: Record<string, unknown>[];
}

const BUNDLE_VERSION = 1;

type TransferState =
  | { status: "idle" | "running" }
  | { status: "done"; message: string }
  | { status: "error"; message: string };

/**
 * Bundle a node subtree to a `{ meshExport, root, nodes[] }` JSON document over the EXISTING verbs
 * (search scope:descendants + getNode) — the same self-describing format the web pack writes, so a
 * bundle exported on one client imports on the other.
 *
 * The web pack downloads the file. RN reports the bundle and keeps it in state; there is no
 * download surface without a file-system dependency.
 */
const NodeExport: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const sourcePath = useText(control.sourcePath);
  const nodeName = useText(control.nodeName) || safeSlug(sourcePath);
  const [state, setState] = useState<TransferState>({ status: "idle" });

  if (!ops?.search || !ops.getNode) return <Notice textKey="unavailable.nodeExport" />;

  const run = () => {
    if (!sourcePath || state.status === "running") return;
    setState({ status: "running" });
    void (async () => {
      try {
        const rows = await ops.search!("scope:descendants", sourcePath, 5000);
        const paths = new Set<string>([sourcePath]);
        for (const r of rows) {
          const p = s(r.path);
          if (p) paths.add(p);
        }
        const nodes: Record<string, unknown>[] = [];
        for (const p of paths) {
          const node = await ops.getNode!(p);
          if (node) nodes.push(node);
        }
        const bundle: MeshBundle = { meshExport: BUNDLE_VERSION, root: sourcePath, nodes };
        const json = JSON.stringify(bundle);
        setState({
          status: "done",
          message: `${nodeName}.mesh.json — ${nodes.length} node(s), ${Math.max(1, Math.round(json.length / 1024))} KB`,
        });
      } catch (e) {
        setState({ status: "error", message: e instanceof Error ? e.message : String(e) });
      }
    })();
  };

  return (
    <View style={{ gap: 8 }}>
      <Pressable accessibilityRole="button" style={styles.button} onPress={run}>
        <Text style={styles.buttonText}>{state.status === "running" ? `${t("menu.export")}…` : t("nodeTransfer.exportSubtree")}</Text>
      </Pressable>
      {state.status === "running" ? <ActivityIndicator /> : null}
      {state.status === "done" ? <Text style={styles.success}>{state.message}</Text> : null}
      {state.status === "error" ? <Text style={styles.error}>{state.message}</Text> : null}
    </View>
  );
};

/** Re-target every node in a bundle from its recorded export root onto `targetPath`. */
export function retargetBundleNode(
  node: Record<string, unknown>,
  root: string,
  targetPath: string,
): Record<string, unknown> {
  const path = str(node.path);
  const suffix = root && path.startsWith(root) ? path.slice(root.length).replace(/^\//, "") : path.split("/").pop() ?? path;
  const newPath = suffix ? `${targetPath}/${suffix}` : targetPath;
  const segments = newPath.split("/");
  return { ...node, path: newPath, namespace: segments.slice(0, -1).join("/"), id: segments[segments.length - 1] };
}

/**
 * Import a bundle produced by NodeExport. RN cannot open a file picker without a document-picker
 * dependency, so the bundle is pasted as JSON — the import LOGIC (validate, re-target from the
 * recorded root, re-create each node) is identical to the web pack's.
 */
const NodeImport: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const targetPath = useText(control.targetPath);
  const [text, setText] = useState("");
  const [state, setState] = useState<TransferState>({ status: "idle" });

  if (!ops?.createNode) return <Notice textKey="unavailable.nodeImport" />;

  const run = () => {
    if (!text.trim() || state.status === "running") return;
    setState({ status: "running" });
    void (async () => {
      try {
        const bundle = JSON.parse(text) as MeshBundle;
        if (!bundle || typeof bundle !== "object" || !Array.isArray(bundle.nodes))
          throw new Error(t("nodeTransfer.notABundle"));
        const root = s(bundle.root);
        let created = 0;
        let failed = 0;
        for (const node of bundle.nodes) {
          try {
            await ops.createNode!(retargetBundleNode(node, root, targetPath));
            created++;
          } catch {
            failed++;
          }
        }
        setState({ status: "done", message: t("nodeTransfer.imported", created) + (failed ? ` (${failed} ✗)` : "") });
      } catch (e) {
        setState({ status: "error", message: e instanceof Error ? e.message : String(e) });
      }
    })();
  };

  return (
    <View style={{ gap: 8 }}>
      <Text style={styles.muted}>{t("nodeTransfer.pasteBundle")}</Text>
      <BundleInput value={text} onChange={setText} />
      <Pressable accessibilityRole="button" style={styles.button} onPress={run}>
        <Text style={styles.buttonText}>{state.status === "running" ? `${t("menu.import")}…` : t("menu.import")}</Text>
      </Pressable>
      {state.status === "running" ? <ActivityIndicator /> : null}
      {state.status === "done" ? <Text style={styles.success}>{state.message}</Text> : null}
      {state.status === "error" ? <Text style={styles.error}>{state.message}</Text> : null}
    </View>
  );
};

function BundleInput({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  return (
    <TextInput
      style={styles.bundleInput}
      value={value}
      onChangeText={onChange}
      multiline
      autoCapitalize="none"
      autoCorrect={false}
      placeholder='{"meshExport":1,"root":"…","nodes":[…]}'
    />
  );
}

// ── FileBrowser ──────────────────────────────────────────────────────────────
function normalizeDir(p: string): string {
  return p.replace(/^\/+|\/+$/g, "");
}

function joinPath(...parts: string[]): string {
  return parts.filter(Boolean).map(normalizeDir).filter(Boolean).join("/");
}

/** A node's content collection, browsable by directory. Upload needs a picker RN doesn't have. */
const FileBrowser: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const ops = useMeshOps();
  const collection = useText(control.collection);
  const nodePath = useText(control.nodePath);
  const initialDir = normalizeDir(s(useResolve(control.path)));

  const [dir, setDir] = useState(initialDir);
  const [listing, setListing] = useState<ContentListing | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const listPath = useMemo(() => joinPath(nodePath, collection, dir), [nodePath, collection, dir]);

  useEffect(() => {
    if (!ops?.listContent || !nodePath || !collection) return;
    let live = true;
    setLoading(true);
    setError(null);
    ops
      .listContent(listPath)
      .then((l) => {
        if (!live) return;
        setListing(l);
        setLoading(false);
      })
      .catch((e) => {
        if (!live) return;
        setError(e instanceof Error ? e.message : String(e));
        setLoading(false);
      });
    return () => {
      live = false;
    };
  }, [ops, listPath, nodePath, collection]);

  if (!ops?.listContent) return <Notice textKey="unavailable.fileBrowser" />;
  if (!nodePath || !collection) return <Notice textKey="empty.noCollectionBound" />;

  const up = () => setDir((d) => d.split("/").slice(0, -1).join("/"));

  return (
    <View style={{ gap: 8 }}>
      <View style={styles.crumbRow}>
        <Text style={styles.muted}>{collection}</Text>
        {dir ? <Text style={styles.muted}>/{dir}</Text> : null}
        {dir ? (
          <Pressable accessibilityRole="button" onPress={up}>
            <Text style={styles.link}>{t("common.up")}</Text>
          </Pressable>
        ) : null}
      </View>
      {loading ? <ActivityIndicator /> : null}
      {error ? <Text style={styles.error}>{error}</Text> : null}
      <ScrollView>
        {(listing?.items ?? []).map((item) => (
          <Pressable
            key={item.path}
            style={styles.fileRow}
            onPress={() => item.kind === "folder" && setDir(joinPath(dir, item.name))}
          >
            <Text style={styles.fileGlyph}>{item.kind === "folder" ? "📁" : "📄"}</Text>
            <Text style={[styles.body, { flex: 1 }]}>{item.name}</Text>
            {item.kind === "folder" && item.itemCount != null ? (
              <Text style={styles.muted}>{item.itemCount}</Text>
            ) : null}
          </Pressable>
        ))}
        {listing && listing.items.length === 0 && !loading ? <Text style={styles.muted}>{t("search.empty")}</Text> : null}
      </ScrollView>
    </View>
  );
};

export const rnDocumentControls: Record<string, ControlComponent> = {
  DocumentSource,
  ExportDocument,
  NodeExport,
  NodeImport,
  FileBrowser,
};

const styles = StyleSheet.create({
  body: { fontSize: 14, color: "#242424" },
  muted: { fontSize: 12, color: "#616161" },
  error: { fontSize: 12, color: "#a4262c" },
  success: { fontSize: 12, color: "#0a7d2c" },
  highlight: { fontSize: 12, color: "#8a6d00" },
  link: { fontSize: 12, color: "#0f6cbd" },
  notice: { backgroundColor: "#eef4fb", borderColor: "#cfe4fa", borderWidth: 1, borderRadius: 6, padding: 10 },
  noticeText: { fontSize: 13, color: "#242424" },
  fileCard: { flexDirection: "row", alignItems: "center", gap: 12, padding: 12, borderRadius: 8, borderWidth: 1, borderColor: "#e1e1e1", backgroundColor: "white" },
  fileGlyph: { fontSize: 22 },
  fileName: { fontSize: 14, fontWeight: "600", color: "#242424" },
  fileRow: { flexDirection: "row", alignItems: "center", gap: 10, paddingVertical: 10, borderBottomWidth: StyleSheet.hairlineWidth, borderColor: "#eee" },
  crumbRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  chipRow: { flexDirection: "row", gap: 8 },
  chip: { paddingVertical: 6, paddingHorizontal: 12, borderRadius: 14, borderWidth: 1, borderColor: "#ccc" },
  chipOn: { backgroundColor: "#0f6cbd", borderColor: "#0f6cbd" },
  chipText: { fontSize: 13, color: "#242424" },
  chipTextOn: { color: "white", fontWeight: "600" },
  checkRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  checkGlyph: { fontSize: 18, color: "#0f6cbd" },
  button: { backgroundColor: "#0f6cbd", paddingVertical: 10, paddingHorizontal: 14, borderRadius: 6, alignItems: "center", alignSelf: "flex-start" },
  buttonText: { color: "white", fontWeight: "600" },
  bundleInput: {
    borderWidth: 1,
    borderColor: "#ccc",
    borderRadius: 6,
    padding: 8,
    fontSize: 12,
    minHeight: 90,
    textAlignVertical: "top",
  },
});
