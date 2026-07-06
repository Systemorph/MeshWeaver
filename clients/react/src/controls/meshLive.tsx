// Live query-backed mesh controls, rendered through the MeshOps contract (live/meshOps.tsx):
//
//   - MeshSearchView ← src/MeshWeaver.Blazor/Components/MeshSearchView.razor
//       MeshSearchControl wire: { title, hiddenQuery (always applied), visibleQuery (user-editable),
//       placeholder, namespace, renderMode ("Flat"|"List"|…), maxColumns, showSearchBox, liveSearch,
//       excludeBasePath, showEmptyMessage, showLoadingIndicator, createHref }
//   - MeshNodeCollectionView ← Components/MeshNodeCollectionView.razor
//       MeshNodeCollectionControl wire: { queries: string[], deletable, showAdd }
//
// Both run their queries through `useMeshOps().search` (the optional mesh-query member — the same
// surface the ThreadChat agent/model selectors use), so any host that wires a MeshOpsProvider gets
// live results; tests inject a fake. Without ops the search renders its box only and the collection
// shows its empty state — no crash, no fake data.

import type { ReactNode } from "react";
import { useEffect, useState } from "react";
import { Avatar, Badge, Button, Card, Input, Link, Spinner, Text } from "@fluentui/react-components";
import { Add20Regular, Search20Regular } from "@fluentui/react-icons";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshLink } from "../area/navigation.js";
import { useMeshOps, type MeshOps } from "../live/meshOps.js";
import { str, useText } from "./common.js";
import { AddressAreaEmbed, isInlineSvg, sanitizeInlineSvg } from "./display.js";

interface NodeResult {
  path: string;
  name: string;
  nodeType: string;
  description: string;
  thumbnail?: string;
}

function toNodeResult(r: Record<string, unknown>): NodeResult {
  const content = (r.content ?? {}) as Record<string, unknown>;
  return {
    path: str(r.path),
    name: str(r.name) || str(r.path).split("/").pop() || str(r.path),
    nodeType: str(r.nodeType),
    description: str(r.description ?? content.description),
    thumbnail: str(content.thumbnailUrl ?? content.imageUrl) || undefined,
  };
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

/** Run a mesh query through MeshOps.search; empty results when ops/search are absent. */
function useMeshQuery(ops: MeshOps | null, query: string, basePath?: string): { results: NodeResult[]; loading: boolean } {
  const [state, setState] = useState<{ results: NodeResult[]; loading: boolean }>({ results: [], loading: false });
  useEffect(() => {
    if (!ops?.search || !query) {
      setState({ results: [], loading: false });
      return;
    }
    let live = true;
    setState((s) => ({ ...s, loading: true }));
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

// ---- MeshSearch -----------------------------------------------------------------------------------

/**
 * A per-item ITEM-AREA card — the Blazor MeshSearchView ItemArea mode: the item delegates its
 * rendering to a layout area hosted on the node's own hub (e.g. the home Pinned row's
 * PinnedThumbnail cards, which carry the unpin overlay). NO outer link: the embedded area's own
 * MeshNodeCard already navigates — wrapping it would nest <a> inside <a> (HTML splits those,
 * duplicating every card link).
 */
function ItemAreaCard({ node, itemArea }: { node: NodeResult; itemArea: string }): ReactNode {
  // minHeight keeps every cell the same card height whether the embedded area has resolved to its
  // card or is still on its (compact) loading skeleton — so the grid row never jumps or leaves a
  // collapsed, half-height cell. display:flex + the child stretching fills the slot.
  return (
    <div
      title={`Open ${node.name}`}
      style={{ position: "relative", minWidth: 0, minHeight: 92, display: "flex", flexDirection: "column" }}
    >
      <AddressAreaEmbed address={node.path} area={itemArea} />
    </div>
  );
}

function ResultCard({ node }: { node: NodeResult }): ReactNode {
  const link = useMeshLink(`/${node.path}`);
  return (
    <Link href={link.href} onClick={link.onClick} style={{ textDecoration: "none" }}>
      <Card style={{ padding: 12, gap: 4, height: "100%" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <Text weight="semibold" style={{ flex: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis" }}>
            {node.name}
          </Text>
          {node.nodeType ? (
            <Badge appearance="outline" size="small">
              {node.nodeType}
            </Badge>
          ) : null}
        </div>
        {node.description ? (
          <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
            {node.description}
          </Text>
        ) : null}
      </Card>
    </Link>
  );
}

function ResultRow({ node }: { node: NodeResult }): ReactNode {
  const link = useMeshLink(`/${node.path}`);
  return (
    <div style={{ display: "flex", alignItems: "baseline", gap: 8, padding: "6px 0", borderBottom: "1px solid var(--colorNeutralStroke3)" }}>
      <Link href={link.href} onClick={link.onClick}>
        <Text weight="semibold">{node.name}</Text>
      </Link>
      {node.nodeType ? (
        <Badge appearance="outline" size="small">
          {node.nodeType}
        </Badge>
      ) : null}
      <Text size={200} style={{ color: "var(--colorNeutralForeground3)", flex: 1, minWidth: 0 }}>
        {node.description}
      </Text>
    </div>
  );
}

export function MeshSearchView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const title = useText(control.title);
  const hiddenQuery = str(useResolve(control.hiddenQuery));
  const initialVisible = str(useResolve(control.visibleQuery));
  const placeholder = str(useResolve(control.placeholder)) || "Search the mesh…";
  const ns = str(useResolve(control.namespace));
  const renderMode = str(useResolve(control.renderMode)) || "Flat";
  const showSearchBox = useResolve(control.showSearchBox) !== false;
  const liveSearch = useResolve(control.liveSearch) !== false;
  const excludeBasePath = useResolve(control.excludeBasePath) !== false;
  const showEmptyMessage = useResolve(control.showEmptyMessage) !== false;
  const showLoading = useResolve(control.showLoadingIndicator) !== false;
  const createHref = str(useResolve(control.createHref));
  const itemArea = str(useResolve(control.itemArea));
  // Honour MaxColumns like Blazor does (WithMaxColumns) — without it the auto-fill grid sprawls to
  // 6-8 narrow columns on a wide screen, so the Pinned/Threads rows never line up with the Blazor
  // layout. GridSpacing (WithGridSpacing) sets the gap.
  const maxColumns = Math.trunc(Number(useResolve(control.maxColumns))) || 0;
  const gridSpacing = Math.trunc(Number(useResolve(control.gridSpacing))) || 12;

  const createLink = useMeshLink(createHref || undefined);
  const [visible, setVisible] = useState(initialVisible);
  const [submitted, setSubmitted] = useState(initialVisible);
  const term = useDebounced(liveSearch ? visible : submitted, 250);
  const query = [hiddenQuery, term].map((s) => s.trim()).filter(Boolean).join(" ");
  const { results, loading } = useMeshQuery(ops, query, ns);
  const items = excludeBasePath && ns ? results.filter((n) => n.path !== ns) : results;
  const list = renderMode === "List";

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
        {title ? (
          <Text weight="semibold" size={400}>
            {title}
          </Text>
        ) : null}
        {showSearchBox ? (
          <Input
            contentBefore={<Search20Regular />}
            placeholder={placeholder}
            value={visible}
            style={{ flex: 1, maxWidth: 420 }}
            onChange={(_, d) => setVisible(d.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") setSubmitted(visible);
            }}
          />
        ) : null}
        {createHref ? (
          <Link href={createLink.href} onClick={createLink.onClick}>
            <Button icon={<Add20Regular />} appearance="subtle" aria-label="Create" />
          </Link>
        ) : null}
      </div>
      {loading && showLoading ? <Spinner size="tiny" label="Searching…" /> : null}
      {!loading && items.length === 0 && showEmptyMessage && query ? (
        <Text italic size={200}>
          No items found.
        </Text>
      ) : null}
      {list ? (
        <div style={{ display: "flex", flexDirection: "column" }}>
          {items.map((n) => (
            <ResultRow key={n.path} node={n} />
          ))}
        </div>
      ) : (
        <div
          style={{
            display: "grid",
            gap: gridSpacing,
            // MaxColumns set → exactly that many equal columns (Blazor parity, left-aligned, no sprawl);
            // unset → responsive auto-fill. minmax(0,1fr) lets cards shrink instead of overflowing.
            gridTemplateColumns:
              maxColumns > 0
                ? `repeat(${maxColumns}, minmax(0, 1fr))`
                : "repeat(auto-fill, minmax(220px, 1fr))",
          }}
        >
          {items.map((n) =>
            itemArea ? <ItemAreaCard key={n.path} node={n} itemArea={itemArea} /> : <ResultCard key={n.path} node={n} />,
          )}
        </div>
      )}
    </div>
  );
}

// ---- MeshNodeCollection ---------------------------------------------------------------------------

export function MeshNodeCollectionView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const queries = (Array.isArray(control.queries) ? control.queries : []).map(str).filter(Boolean);
  const showAdd = control.showAdd !== false;
  const [items, setItems] = useState<NodeResult[] | null>(null);

  useEffect(() => {
    if (!ops?.search || queries.length === 0) {
      setItems([]);
      return;
    }
    let live = true;
    Promise.all(queries.map((q) => ops.search!(q).catch(() => [] as Record<string, unknown>[])))
      .then((all) => {
        if (!live) return;
        const merged = new Map<string, NodeResult>();
        for (const n of all.flat().map(toNodeResult)) if (n.path) merged.set(n.path, n);
        setItems([...merged.values()]);
      })
      .catch(() => {
        if (live) setItems([]);
      });
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, queries.join("")]);

  if (items == null) return <Spinner size="tiny" />;
  if (items.length === 0 && !showAdd)
    return (
      <Text size={200} style={{ color: "var(--colorNeutralForeground3)", padding: 8 }}>
        No items.
      </Text>
    );
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
      {items.map((n) => (
        <CollectionRow key={n.path} node={n} />
      ))}
    </div>
  );
}

function CollectionRow({ node }: { node: NodeResult }): ReactNode {
  const link = useMeshLink(`/${node.path}`);
  return (
    <Link href={link.href} onClick={link.onClick} style={{ textDecoration: "none" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "6px 8px", borderRadius: 6 }}>
        {node.thumbnail && isInlineSvg(node.thumbnail) ? (
          // Inline SVG node Icon — an <Avatar image src> can't take raw markup (it would break and
          // fall back to initials), so render the SVG directly in an avatar-sized box.
          <span
            aria-hidden
            style={{ display: "inline-flex", width: 28, height: 28, flexShrink: 0, borderRadius: "50%", overflow: "hidden" }}
            dangerouslySetInnerHTML={{ __html: sanitizeInlineSvg(node.thumbnail) }}
          />
        ) : (
          <Avatar name={node.name} image={node.thumbnail ? { src: node.thumbnail } : undefined} size={28} />
        )}
        <div style={{ display: "flex", flexDirection: "column", minWidth: 0 }}>
          <Text weight="semibold" style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
            {node.name}
          </Text>
          <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
            {node.nodeType}
          </Text>
        </div>
      </div>
    </Link>
  );
}
