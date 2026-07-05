// Document controls — the React implementations of DocumentSourceControl and ExportDocumentControl
// (previously long-tail placeholders). Both mirror the Blazor views:
//   - DocumentSource: renders a document already served at a /static/… URL — an <iframe> for
//     browser-native types (PDF), a download card otherwise, plus the highlighted passage.
//   - ExportDocument: a small options form that runs the server export (ExportDocumentRequest →
//     Activity → RenderedDocument bytes) through MeshOps.exportDocument and triggers a download.

import type { ReactNode } from "react";
import { useState } from "react";
import { Button, Checkbox, Link, MessageBar, MessageBarBody, Radio, RadioGroup, Spinner, Text } from "@fluentui/react-components";
import { ArrowDownload20Regular } from "@fluentui/react-icons";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshOps } from "../live/meshOps.js";
import { str, useText } from "./common.js";

// ---- DocumentSource ------------------------------------------------------------------------------

function DocumentSourceView({ control }: { control: UiControl }): ReactNode {
  const fileUrl = useText(control.fileUrl);
  const mime = useText(control.mime);
  const highlight = useText(control.highlight);
  const fileName = useText(control.fileName) || safeName(fileUrl);
  if (!fileUrl) return null;
  const isPdf = /pdf/i.test(mime) || /\.pdf(\?|$)/i.test(fileUrl);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8, width: "100%", minWidth: 0 }}>
      {isPdf ? (
        <iframe
          src={fileUrl}
          title={fileName}
          style={{ width: "100%", height: "70vh", border: "1px solid var(--colorNeutralStroke2)", borderRadius: 4 }}
        />
      ) : (
        <div
          style={{
            border: "1px solid var(--colorNeutralStroke2)",
            borderRadius: 6,
            padding: 16,
            display: "flex",
            flexDirection: "column",
            gap: 8,
          }}
        >
          <Text weight="semibold">{fileName}</Text>
          <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
            In-browser preview isn't available for this file type — download it to view.
          </Text>
        </div>
      )}
      {highlight ? (
        <Text size={200} italic style={{ color: "var(--colorNeutralForeground3)" }}>
          Highlighted passage: “{highlight}”
        </Text>
      ) : null}
      {/* A /static/… URL is an ORIGIN resource, not an app route — keep it root-absolute so the
          browser fetches it directly. Routing it through useMeshLink would rewrite it under the
          host basePath (e.g. /next/static/… in portal-next) and 404. */}
      <Link href={fileUrl} download style={{ display: "inline-flex", alignItems: "center", gap: 4, fontSize: 12 }}>
        <ArrowDownload20Regular fontSize={16} /> Download {fileName}
      </Link>
    </div>
  );
}

function safeName(url: string): string {
  const last = url.split("/").pop() || "document";
  try {
    return decodeURIComponent(last.split("?")[0]) || "document";
  } catch {
    return last.split("?")[0] || "document";
  }
}

// ---- ExportDocument ------------------------------------------------------------------------------

type ExportState = { status: "idle" } | { status: "exporting" } | { status: "error"; message: string };

function ExportDocumentView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const sourcePath = useText(control.sourcePath);
  const nodeName = useText(control.nodeName);
  const hasDescendants = !!useResolve(control.hasDescendants);
  const defaultFormat = (str(useResolve(control.defaultFormat)) || "pdf").toLowerCase() === "docx" ? "docx" : "pdf";

  const [format, setFormat] = useState<"pdf" | "docx">(defaultFormat as "pdf" | "docx");
  const [includeChildren, setIncludeChildren] = useState(false);
  const [state, setState] = useState<ExportState>({ status: "idle" });

  if (!ops?.exportDocument)
    return (
      <MessageBar intent="info">
        <MessageBarBody>Document export isn't available in this frontend.</MessageBarBody>
      </MessageBar>
    );

  const run = () => {
    if (!sourcePath || state.status === "exporting") return;
    setState({ status: "exporting" });
    ops
      .exportDocument!(sourcePath, { format, title: nodeName || undefined, includeChildren })
      .then((doc) => {
        triggerDownload(doc.bytes, doc.fileName, doc.mimeType);
        setState({ status: "idle" });
      })
      .catch((e) => setState({ status: "error", message: e instanceof Error ? e.message : String(e) }));
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12, maxWidth: 420 }}>
      <div>
        <Text weight="semibold" size={300}>
          Format
        </Text>
        <RadioGroup layout="horizontal" value={format} onChange={(_, d) => setFormat(d.value === "docx" ? "docx" : "pdf")}>
          <Radio value="pdf" label="PDF" />
          <Radio value="docx" label="Word (.docx)" />
        </RadioGroup>
      </div>
      {hasDescendants ? (
        <Checkbox
          checked={includeChildren}
          label="Include child pages"
          onChange={(_, d) => setIncludeChildren(!!d.checked)}
        />
      ) : null}
      <Button
        appearance="primary"
        icon={state.status === "exporting" ? <Spinner size="tiny" /> : <ArrowDownload20Regular />}
        disabled={state.status === "exporting" || !sourcePath}
        onClick={run}
      >
        {state.status === "exporting" ? "Exporting…" : `Export ${format.toUpperCase()}`}
      </Button>
      {state.status === "error" ? (
        <MessageBar intent="error">
          <MessageBarBody>Export failed: {state.message}</MessageBarBody>
        </MessageBar>
      ) : null}
    </div>
  );
}

/** Trigger a browser download of raw bytes without leaking the object URL. */
export function triggerDownload(bytes: Uint8Array, fileName: string, mimeType: string): void {
  // Copy into a plain ArrayBuffer-backed view — TS's Blob rejects Uint8Array<ArrayBufferLike>
  // (it could be SharedArrayBuffer-backed); the copy is also what guarantees a detached buffer.
  const part = bytes.slice().buffer;
  const blob = new Blob([part], { type: mimeType || "application/octet-stream" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  // Revoke after the click has been dispatched.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

export const documentControls = {
  DocumentSource: DocumentSourceView,
  ExportDocument: ExportDocumentView,
};
