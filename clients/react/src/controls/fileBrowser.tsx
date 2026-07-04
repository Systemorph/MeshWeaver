// FileBrowser — the React implementation of FileBrowserControl (formerly a placeholder). Browses a
// content collection over the REST content surface: list (POST /api/mesh/content/list) + download
// (the /content/{node}/{collection}{path} static URL) + upload (POST /api/mesh/upload). The owning
// node path comes from control.nodePath (stamped server-side) or, as a fallback, the first segment
// of control.urlBasePath (/{node}/{area}/{collection}); the collection lives on that node's hub.

import type { ReactNode } from "react";
import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Link, MessageBar, MessageBarBody, Spinner, Text } from "@fluentui/react-components";
import { ArrowUpload20Regular, Folder20Regular, Document20Regular, ArrowUp20Regular } from "@fluentui/react-icons";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshOps, type ContentItem, type ContentListing } from "../live/meshOps.js";
import { str, useText } from "./common.js";

function FileBrowserView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const collection = useText(control.collection);
  const nodePath = useText(control.nodePath) || nodeFromUrlBase(str(useResolve(control.urlBasePath)));
  const readOnly = !!useResolve(control.readOnly);
  const initialDir = normalizeDir(str(useResolve(control.path)));

  const [dir, setDir] = useState(initialDir);
  const [listing, setListing] = useState<ContentListing | null>(null);
  const [state, setState] = useState<{ status: "idle" | "loading" | "uploading" } | { status: "error"; message: string }>({ status: "loading" });
  const fileRef = useRef<HTMLInputElement>(null);

  const listPath = useMemo(() => joinPath(nodePath, collection, dir), [nodePath, collection, dir]);

  const refresh = useMemo(
    () => () => {
      if (!ops?.listContent || !nodePath || !collection) return;
      setState({ status: "loading" });
      ops
        .listContent(listPath)
        .then((l) => {
          setListing(l);
          setState({ status: "idle" });
        })
        .catch((e) => setState({ status: "error", message: e instanceof Error ? e.message : String(e) }));
    },
    [ops, listPath, nodePath, collection],
  );

  useEffect(refresh, [refresh]);

  if (!ops?.listContent)
    return (
      <MessageBar intent="info">
        <MessageBarBody>The file browser isn't available in this frontend.</MessageBarBody>
      </MessageBar>
    );
  if (!nodePath || !collection)
    return (
      <MessageBar intent="warning">
        <MessageBarBody>This file browser is missing its node/collection context.</MessageBarBody>
      </MessageBar>
    );

  const canWrite = !readOnly && listing?.editable !== false && !!ops.uploadContent;

  const onUpload = (file: File) => {
    if (!ops.uploadContent) return;
    setState({ status: "uploading" });
    ops
      .uploadContent(`${joinPath(nodePath, collection, dir)}/${file.name}`.replace(/\/{2,}/g, "/"), file)
      .then(() => refresh())
      .catch((e) => setState({ status: "error", message: e instanceof Error ? e.message : String(e) }));
  };

  const parent = parentDir(dir);
  const items = listing?.items ?? [];

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8, width: "100%", minWidth: 0 }}>
      {/* Breadcrumb + upload */}
      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
        <Text weight="semibold" style={{ flex: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis" }}>
          {collection}
          {dir}
        </Text>
        {canWrite ? (
          <>
            <input
              ref={fileRef}
              type="file"
              style={{ display: "none" }}
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) onUpload(f);
                e.target.value = "";
              }}
            />
            <Button
              size="small"
              icon={state.status === "uploading" ? <Spinner size="tiny" /> : <ArrowUpload20Regular />}
              disabled={state.status === "uploading"}
              onClick={() => fileRef.current?.click()}
            >
              {state.status === "uploading" ? "Uploading…" : "Upload"}
            </Button>
          </>
        ) : null}
      </div>

      {state.status === "error" ? (
        <MessageBar intent="error">
          <MessageBarBody>{state.message}</MessageBarBody>
        </MessageBar>
      ) : null}

      <div style={{ border: "1px solid var(--colorNeutralStroke2)", borderRadius: 6, overflow: "hidden" }}>
        {parent !== null ? (
          <Row onClick={() => setDir(parent!)} icon={<ArrowUp20Regular />} name=".." muted />
        ) : null}
        {state.status === "loading" ? (
          <div style={{ padding: 16, display: "flex", justifyContent: "center" }}>
            <Spinner size="tiny" label="Loading…" />
          </div>
        ) : items.length === 0 ? (
          <div style={{ padding: 16 }}>
            <Text size={200} italic style={{ color: "var(--colorNeutralForeground3)" }}>
              This folder is empty.
            </Text>
          </div>
        ) : (
          items.map((it) => (
            <FileRow
              key={it.path}
              item={it}
              onOpenFolder={() => setDir(normalizeDir(it.path))}
              downloadUrl={fileUrl(nodePath, collection, it.path)}
            />
          ))
        )}
      </div>
    </div>
  );
}

function FileRow({ item, onOpenFolder, downloadUrl }: { item: ContentItem; onOpenFolder: () => void; downloadUrl: string }): ReactNode {
  if (item.kind === "folder")
    return <Row onClick={onOpenFolder} icon={<Folder20Regular />} name={item.name} meta={item.itemCount != null ? `${item.itemCount} item${item.itemCount === 1 ? "" : "s"}` : undefined} />;
  return (
    <div style={rowStyle}>
      <Document20Regular />
      <Link href={downloadUrl} download style={{ flex: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
        {item.name}
      </Link>
      {item.lastModified ? (
        <Text size={100} style={{ color: "var(--colorNeutralForeground3)" }}>
          {new Date(item.lastModified).toLocaleDateString()}
        </Text>
      ) : null}
    </div>
  );
}

const rowStyle: React.CSSProperties = {
  display: "flex",
  alignItems: "center",
  gap: 8,
  padding: "6px 10px",
  borderBottom: "1px solid var(--colorNeutralStroke3)",
};

function Row({ onClick, icon, name, meta, muted }: { onClick: () => void; icon: ReactNode; name: string; meta?: string; muted?: boolean }): ReactNode {
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={onClick}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault(); // Space would otherwise scroll the page on a role="button" element
          onClick();
        }
      }}
      style={{ ...rowStyle, cursor: "pointer" }}
    >
      {icon}
      <Text weight={muted ? "regular" : "semibold"} style={{ flex: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", color: muted ? "var(--colorNeutralForeground3)" : undefined }}>
        {name}
      </Text>
      {meta ? (
        <Text size={100} style={{ color: "var(--colorNeutralForeground3)" }}>
          {meta}
        </Text>
      ) : null}
    </div>
  );
}

// ---- path helpers --------------------------------------------------------------------------------

/** Owning node from a "/{node}/{area}/{collection}" UrlBasePath fallback. */
function nodeFromUrlBase(urlBase: string): string {
  return urlBase.split("/").filter(Boolean)[0] ?? "";
}

/** A collection-relative directory, always leading-slash, no trailing slash ("/" is the root). */
function normalizeDir(p: string): string {
  const trimmed = (p || "/").replace(/\/+$/, "");
  return trimmed.startsWith("/") ? trimmed || "/" : `/${trimmed}`;
}

/** The parent directory, or null at the collection root. */
function parentDir(dir: string): string | null {
  if (dir === "/" || dir === "") return null;
  const idx = dir.lastIndexOf("/");
  return idx <= 0 ? "/" : dir.slice(0, idx);
}

/** The content-list path: {node}/{collection}[/{dir}] (dir slashes normalized out). */
function joinPath(node: string, collection: string, dir: string): string {
  const d = dir.replace(/^\/+|\/+$/g, "");
  return d ? `${node}/${collection}/${d}` : `${node}/${collection}`;
}

/** The static content URL for a file (each segment encoded). */
function fileUrl(node: string, collection: string, itemPath: string): string {
  const rel = itemPath.replace(/^\/+/, "");
  const enc = (s: string) => s.split("/").map(encodeURIComponent).join("/");
  return `/content/${enc(node)}/${encodeURIComponent(collection)}/${enc(rel)}`;
}

export const fileBrowserControls = {
  FileBrowser: FileBrowserView,
};
