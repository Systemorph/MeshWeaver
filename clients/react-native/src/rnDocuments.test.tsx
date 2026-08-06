import { describe, expect, it, vi } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import {
  MeshOpsProvider,
  RegistryProvider,
  ScopeProvider,
  RenderArea,
  StaticAreaSource,
  type AreaTree,
  type MeshNodeState,
  type MeshOps,
  localize,
} from "@meshweaver/react/core";

import { rnPack } from "./rnPack";
import { retargetBundleNode } from "./rnDocuments";

// Assert against the CATALOG, not a literal — that also pins that these strings are localized.
const en = (key: string) => localize(key, "en");

type Json = ReactTestRendererJSON;

function nodeStream(...states: MeshNodeState[]): AsyncIterable<MeshNodeState> {
  return {
    async *[Symbol.asyncIterator]() {
      for (const s of states) yield s;
      await new Promise(() => {});
    },
  };
}

function fakeOps(over: Partial<MeshOps> = {}): MeshOps {
  return {
    watch: () => nodeStream(),
    startThread: async () => ({ path: "t" }),
    submitMessage: async () => "m",
    patch: () => {},
    ...over,
  } as MeshOps;
}

async function renderLive(tree: AreaTree, ops: MeshOps | null): Promise<TestRenderer.ReactTestRenderer> {
  let r!: TestRenderer.ReactTestRenderer;
  const inner = (
    <ScopeProvider source={new StaticAreaSource(tree)} area="main">
      <RenderArea areaKey="main" />
    </ScopeProvider>
  );
  await TestRenderer.act(async () => {
    r = TestRenderer.create(
      <RegistryProvider pack={rnPack}>{ops ? <MeshOpsProvider ops={ops}>{inner}</MeshOpsProvider> : inner}</RegistryProvider>,
    );
  });
  return r;
}

function* walk(node: Json | Json[] | null): Generator<Json> {
  if (node == null) return;
  if (Array.isArray(node)) {
    for (const n of node) yield* walk(n);
    return;
  }
  yield node;
  if (node.children) for (const c of node.children) yield* walk(c as Json);
}

function textOf(node: Json): string {
  let out = "";
  for (const c of node.children ?? []) {
    if (typeof c === "string") out += c;
    else if (c && typeof c === "object") out += textOf(c as Json);
  }
  return out;
}

const allText = (r: TestRenderer.ReactTestRenderer) =>
  [...walk(r.toJSON() as Json)].filter((n) => n.type === "Text").map(textOf);
const find = (r: TestRenderer.ReactTestRenderer, pred: (n: Json) => boolean) =>
  [...walk(r.toJSON() as Json)].find(pred);

// ---- retargetBundleNode (pure) ----------------------------------------------------------------

describe("retargetBundleNode", () => {
  it("re-homes a descendant under the target, preserving its relative path", () => {
    const out = retargetBundleNode({ path: "src/Docs/Guide/Intro", id: "Intro" }, "src/Docs", "dst/Imported");
    expect(out.path).toBe("dst/Imported/Guide/Intro");
    expect(out.namespace).toBe("dst/Imported/Guide");
    expect(out.id).toBe("Intro");
  });

  it("maps the export ROOT itself onto the target", () => {
    const out = retargetBundleNode({ path: "src/Docs" }, "src/Docs", "dst/Imported");
    expect(out.path).toBe("dst/Imported");
    expect(out.namespace).toBe("dst");
  });

  it("falls back to the leaf name when a node lies outside the recorded root", () => {
    const out = retargetBundleNode({ path: "elsewhere/Stray" }, "src/Docs", "dst/Imported");
    expect(out.path).toBe("dst/Imported/Stray");
  });
});

// ---- DocumentSource ---------------------------------------------------------------------------

describe("DocumentSource", () => {
  it("renders an openable card with the file name, mime and highlight", async () => {
    const r = await renderLive(
      {
        areas: {
          main: {
            $type: "DocumentSource",
            fileUrl: "https://cdn/report.pdf",
            mime: "application/pdf",
            highlight: "page 3",
          },
        },
      },
      null,
    );
    const text = allText(r);
    expect(text).toContain("report.pdf");
    expect(text).toContain("application/pdf");
    expect(text).toContain("page 3");
    expect(find(r, (n) => n.type === "Pressable")).toBeDefined();
  });

  it("renders nothing without a file url", async () => {
    const r = await renderLive({ areas: { main: { $type: "DocumentSource" } } }, null);
    expect(r.toJSON()).toBeNull();
  });
});

// ---- ExportDocument ---------------------------------------------------------------------------

describe("ExportDocument", () => {
  it("says export is unavailable when the host wires no exportDocument op", async () => {
    const r = await renderLive({ areas: { main: { $type: "ExportDocument", sourcePath: "a/B" } } }, fakeOps());
    expect(allText(r).join(" ")).toContain(en("unavailable.documentExport"));
  });

  it("runs the export with the chosen format and reports the produced document", async () => {
    const exportDocument = vi.fn(async () => ({
      fileName: "Report.pdf",
      mimeType: "application/pdf",
      bytes: new Uint8Array(4096),
    }));
    const r = await renderLive(
      { areas: { main: { $type: "ExportDocument", sourcePath: "a/B", nodeName: "Report" } } },
      fakeOps({ exportDocument }),
    );
    const btn = find(r, (n) => n.type === "Pressable" && n.props.accessibilityRole === "button")!;
    await TestRenderer.act(async () => btn.props.onPress());
    expect(exportDocument).toHaveBeenCalledWith("a/B", { format: "pdf", title: "Report", includeChildren: false });
    expect(allText(r).join(" ")).toContain("Report.pdf");
  });

  it("surfaces an export failure instead of silently doing nothing", async () => {
    const exportDocument = vi.fn(async () => {
      throw new Error("renderer unavailable");
    });
    const r = await renderLive(
      { areas: { main: { $type: "ExportDocument", sourcePath: "a/B" } } },
      fakeOps({ exportDocument }),
    );
    const btn = find(r, (n) => n.type === "Pressable" && n.props.accessibilityRole === "button")!;
    await TestRenderer.act(async () => btn.props.onPress());
    expect(allText(r).join(" ")).toContain("renderer unavailable");
  });
});

// ---- NodeExport / NodeImport -------------------------------------------------------------------

describe("NodeExport", () => {
  it("bundles the root plus every descendant read through getNode", async () => {
    const search = vi.fn(async () => [{ path: "a/B/child" }]);
    const getNode = vi.fn(async (p: string) => ({ path: p, name: p.split("/").pop() }));
    const r = await renderLive(
      { areas: { main: { $type: "NodeExport", sourcePath: "a/B", nodeName: "B" } } },
      fakeOps({ search, getNode }),
    );
    const btn = find(r, (n) => n.type === "Pressable" && n.props.accessibilityRole === "button")!;
    await TestRenderer.act(async () => btn.props.onPress());
    expect(search).toHaveBeenCalledWith("scope:descendants", "a/B", 5000);
    expect(getNode).toHaveBeenCalledWith("a/B");
    expect(getNode).toHaveBeenCalledWith("a/B/child");
    expect(allText(r).join(" ")).toContain("2 node(s)");
  });

  it("says export is unavailable without the search/getNode ops", async () => {
    const r = await renderLive({ areas: { main: { $type: "NodeExport", sourcePath: "a/B" } } }, fakeOps());
    expect(allText(r).join(" ")).toContain(en("unavailable.nodeExport"));
  });
});

describe("NodeImport", () => {
  it("re-targets every bundled node onto the target and creates it", async () => {
    const createNode = vi.fn(async (_node: Record<string, unknown>) => undefined);
    const r = await renderLive(
      { areas: { main: { $type: "NodeImport", targetPath: "dst/Here" } } },
      fakeOps({ createNode }),
    );
    const input = find(r, (n) => n.type === "TextInput")!;
    const bundle = JSON.stringify({
      meshExport: 1,
      root: "src/Docs",
      nodes: [{ path: "src/Docs" }, { path: "src/Docs/Guide" }],
    });
    await TestRenderer.act(async () => input.props.onChangeText(bundle));
    const btn = find(r, (n) => n.type === "Pressable" && n.props.accessibilityRole === "button")!;
    await TestRenderer.act(async () => btn.props.onPress());
    expect(createNode).toHaveBeenCalledTimes(2);
    expect(createNode.mock.calls[0][0]).toMatchObject({ path: "dst/Here" });
    expect(createNode.mock.calls[1][0]).toMatchObject({ path: "dst/Here/Guide" });
    expect(allText(r).join(" ")).toContain(en("nodeTransfer.imported").replace("{0}", "2"));
  });

  it("rejects a document that is not a mesh bundle", async () => {
    const r = await renderLive(
      { areas: { main: { $type: "NodeImport", targetPath: "dst/Here" } } },
      fakeOps({ createNode: async () => undefined }),
    );
    const input = find(r, (n) => n.type === "TextInput")!;
    await TestRenderer.act(async () => input.props.onChangeText('{"hello":"world"}'));
    const btn = find(r, (n) => n.type === "Pressable" && n.props.accessibilityRole === "button")!;
    await TestRenderer.act(async () => btn.props.onPress());
    expect(allText(r).join(" ")).toContain(en("nodeTransfer.notABundle"));
  });

  it("is its own component — never an alias of NodeExport", () => {
    expect(rnPack.controls.NodeImport).not.toBe(rnPack.controls.NodeExport);
  });
});

// ---- FileBrowser ---------------------------------------------------------------------------------

describe("FileBrowser", () => {
  const listing = {
    collection: "Files",
    path: "a/B/Files",
    editable: true,
    items: [
      { kind: "folder" as const, name: "sub", path: "a/B/Files/sub", itemCount: 2 },
      { kind: "file" as const, name: "notes.md", path: "a/B/Files/notes.md" },
    ],
  };

  it("lists the bound collection through listContent", async () => {
    const listContent = vi.fn(async () => listing);
    const r = await renderLive(
      { areas: { main: { $type: "FileBrowser", nodePath: "a/B", collection: "Files" } } },
      fakeOps({ listContent }),
    );
    await TestRenderer.act(async () => {
      await new Promise((res) => setTimeout(res, 10));
    });
    expect(listContent).toHaveBeenCalledWith("a/B/Files");
    const text = allText(r);
    expect(text).toContain("sub");
    expect(text).toContain("notes.md");
  });

  it("descends into a folder by re-listing the child directory", async () => {
    const listContent = vi.fn(async () => listing);
    const r = await renderLive(
      { areas: { main: { $type: "FileBrowser", nodePath: "a/B", collection: "Files" } } },
      fakeOps({ listContent }),
    );
    await TestRenderer.act(async () => {
      await new Promise((res) => setTimeout(res, 10));
    });
    const folderRow = find(r, (n) => n.type === "Pressable" && textOf(n).includes("sub"))!;
    await TestRenderer.act(async () => folderRow.props.onPress());
    await TestRenderer.act(async () => {
      await new Promise((res) => setTimeout(res, 10));
    });
    expect(listContent).toHaveBeenCalledWith("a/B/Files/sub");
  });

  it("says so when the host wires no listContent op", async () => {
    const r = await renderLive(
      { areas: { main: { $type: "FileBrowser", nodePath: "a/B", collection: "Files" } } },
      fakeOps(),
    );
    expect(allText(r).join(" ")).toContain(en("unavailable.fileBrowser"));
  });

  it("surfaces a listing failure", async () => {
    const listContent = vi.fn(async () => {
      throw new Error("collection missing");
    });
    const r = await renderLive(
      { areas: { main: { $type: "FileBrowser", nodePath: "a/B", collection: "Files" } } },
      fakeOps({ listContent }),
    );
    await TestRenderer.act(async () => {
      await new Promise((res) => setTimeout(res, 10));
    });
    expect(allText(r).join(" ")).toContain("collection missing");
  });
});
