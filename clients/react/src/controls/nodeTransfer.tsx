// Node transfer controls — the React implementations of NodeExportControl / NodeImportControl
// (previously long-tail placeholders). The Blazor views are stubbed (their low-level
// IMeshExport/ImportService were removed in the 2026-05-12 persistence cull), so there is no ZIP
// format to interop with; these implement a real, self-consistent JSON-BUNDLE round-trip over the
// EXISTING verbs — no revived service, no new server surface:
//   - Export: query the subtree (search scope:descendants) + read each node's raw JSON (getNode),
//     bundle to a { meshExport, root, nodes[] } document, and download it.
//   - Import: read a bundle, RE-TARGET each node's path/namespace from its export root onto the
//     chosen target, and re-create it (createNode → the node's owner partition).

import type { ReactNode } from "react";
import { useRef, useState } from "react";
import { Button, Field, Input, MessageBar, MessageBarBody, Spinner, Switch, Text } from "@fluentui/react-components";
import { ArrowDownload20Regular, ArrowUpload20Regular } from "@fluentui/react-icons";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshOps } from "../live/meshOps.js";
import { str, useText } from "./common.js";
import { triggerDownload } from "./documentControls.js";

/** The bundle wire shape — self-describing so an import can re-target from the recorded root. */
interface MeshBundle {
  meshExport: number;
  root: string;
  exportedAt?: string;
  nodes: Record<string, unknown>[];
}

const BUNDLE_VERSION = 1;

// ---- NodeExport ----------------------------------------------------------------------------------

function NodeExportView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const sourcePath = useText(control.sourcePath);
  const nodeName = useText(control.nodeName) || safeSlug(sourcePath);
  const [state, setState] = useState<{ status: "idle" | "exporting" } | { status: "error"; message: string }>({ status: "idle" });

  if (!ops?.search || !ops.getNode)
    return unavailable("Node export isn't available in this frontend.");

  const run = () => {
    if (!sourcePath || state.status === "exporting") return;
    setState({ status: "exporting" });
    (async () => {
      // The root plus every descendant. search(basePath) scopes to the subtree.
      const rows = await ops.search!("scope:descendants", sourcePath, 5000);
      const paths = new Set<string>([sourcePath]);
      for (const r of rows) {
        const p = str(r.path);
        if (p) paths.add(p);
      }
      const nodes: Record<string, unknown>[] = [];
      for (const p of paths) {
        const node = await ops.getNode!(p);
        if (node) nodes.push(node);
      }
      const bundle: MeshBundle = { meshExport: BUNDLE_VERSION, root: sourcePath, nodes };
      const bytes = new TextEncoder().encode(JSON.stringify(bundle, null, 2));
      triggerDownload(bytes, `${nodeName}.mesh.json`, "application/json");
      setState({ status: "idle" });
    })().catch((e) => setState({ status: "error", message: e instanceof Error ? e.message : String(e) }));
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 10, maxWidth: 420 }}>
      <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
        Downloads <b>{sourcePath || "this node"}</b> and its descendants as a portable JSON bundle.
      </Text>
      <Button
        appearance="primary"
        icon={state.status === "exporting" ? <Spinner size="tiny" /> : <ArrowDownload20Regular />}
        disabled={state.status === "exporting" || !sourcePath}
        onClick={run}
      >
        {state.status === "exporting" ? "Exporting…" : "Export node"}
      </Button>
      {state.status === "error" ? errorBar(state.message) : null}
    </div>
  );
}

// ---- NodeImport ----------------------------------------------------------------------------------

type ImportState =
  | { status: "idle" }
  | { status: "importing" }
  | { status: "done"; created: number; failed: number }
  | { status: "error"; message: string };

function NodeImportView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const targetPath = useText(control.targetPath);
  const forceDefault = !!useResolve(control.force);
  const [force, setForce] = useState(forceDefault);
  const [state, setState] = useState<ImportState>({ status: "idle" });
  const fileRef = useRef<HTMLInputElement>(null);

  if (!ops?.createNode)
    return unavailable("Node import isn't available in this frontend.");

  const onFile = (file: File) => {
    setState({ status: "importing" });
    (async () => {
      const text = await file.text();
      const bundle = JSON.parse(text) as MeshBundle;
      if (!bundle || typeof bundle !== "object" || !Array.isArray(bundle.nodes))
        throw new Error("Not a MeshWeaver node bundle (.mesh.json).");
      const root = str(bundle.root);
      let created = 0;
      let failed = 0;
      for (const raw of bundle.nodes) {
        const node = retarget(raw, root, targetPath);
        if (force) delete (node as Record<string, unknown>).createdBy; // let the server re-stamp
        try {
          await ops.createNode!(node);
          created++;
        } catch {
          failed++; // e.g. already exists without force — counted, not fatal
        }
      }
      setState({ status: "done", created, failed });
    })().catch((e) => setState({ status: "error", message: e instanceof Error ? e.message : String(e) }));
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 10, maxWidth: 460 }}>
      <Field label={`Import into: ${targetPath || "(target unset)"}`}>
        <input
          ref={fileRef}
          type="file"
          accept=".json,application/json"
          style={{ display: "none" }}
          onChange={(e) => {
            const f = e.target.files?.[0];
            if (f) onFile(f);
            e.target.value = ""; // allow re-selecting the same file
          }}
        />
        <Input readOnly value="" placeholder="Choose a .mesh.json bundle…" contentAfter={
          <Button
            appearance="primary"
            size="small"
            icon={state.status === "importing" ? <Spinner size="tiny" /> : <ArrowUpload20Regular />}
            disabled={state.status === "importing" || !targetPath}
            onClick={() => fileRef.current?.click()}
          >
            {state.status === "importing" ? "Importing…" : "Choose file"}
          </Button>
        } />
      </Field>
      <Switch checked={force} label="Force (overwrite existing)" onChange={(_, d) => setForce(!!d.checked)} />
      {state.status === "done" ? (
        <MessageBar intent={state.failed > 0 ? "warning" : "success"}>
          <MessageBarBody>
            Imported {state.created} node{state.created === 1 ? "" : "s"}
            {state.failed > 0 ? `; ${state.failed} skipped (already present — enable Force to overwrite)` : ""}.
          </MessageBarBody>
        </MessageBar>
      ) : null}
      {state.status === "error" ? errorBar(state.message) : null}
    </div>
  );
}

// ---- helpers -------------------------------------------------------------------------------------

/** Re-point a node's path + namespace from its export root onto the import target. */
function retarget(raw: Record<string, unknown>, root: string, target: string): Record<string, unknown> {
  const node = { ...raw };
  const rebase = (value: unknown): unknown => {
    const s = str(value as never);
    if (!s || !root) return value;
    if (s === root) return target;
    if (s.startsWith(root + "/")) return target + s.slice(root.length);
    return value;
  };
  if ("path" in node) node.path = rebase(node.path);
  if ("namespace" in node) node.namespace = rebase(node.namespace);
  if ("mainNode" in node) node.mainNode = rebase(node.mainNode);
  return node;
}

function safeSlug(path: string): string {
  return (path.split("/").pop() || "node").replace(/[^a-zA-Z0-9._-]+/g, "-") || "node";
}

function unavailable(message: string): ReactNode {
  return (
    <MessageBar intent="info">
      <MessageBarBody>{message}</MessageBarBody>
    </MessageBar>
  );
}

function errorBar(message: string): ReactNode {
  return (
    <MessageBar intent="error">
      <MessageBarBody>{message}</MessageBarBody>
    </MessageBar>
  );
}

export const nodeTransferControls = {
  NodeExport: NodeExportView,
  NodeImport: NodeImportView,
};
