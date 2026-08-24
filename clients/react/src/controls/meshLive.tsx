// Live query-backed mesh controls, rendered through the MeshOps contract (live/meshOps.tsx):
//
//   - MeshSearchView ← src/MeshWeaver.Blazor/Components/MeshSearchView.razor
//       MeshSearchControl wire: { title, hiddenQuery (always applied), visibleQuery (user-editable),
//       placeholder, namespace, renderMode ("Flat"|"List"|"Grouped"|"Icons"|…), maxColumns,
//       minItemWidth, showSearchBox, showViewOptions, liveSearch, excludeBasePath, showEmptyMessage,
//       showLoadingIndicator, createHref, scopeTabs, sortOptions, navigateToMainNode,
//       groupByFrequency, grouping, sections, grid }
//   - MeshNodeCollectionView ← Components/MeshNodeCollectionView.razor
//       MeshNodeCollectionControl wire: { queries: string[], deletable, showAdd }
//
// Both run their queries through `useMeshOps().search` (the optional mesh-query member — the same
// surface the ThreadChat agent/model selectors use), so any host that wires a MeshOpsProvider gets
// live results; tests inject a fake. Without ops the search renders its box only and the collection
// shows its empty state — no crash, no fake data.
//
// The home-design semantics (scope tabs, union queries, the Icons grid, NavigateToMainNode,
// SortByAccess, grouped-by-type sections) live in the PURE shared model
// (controls/meshSearchModel.ts) so this pack and the RN pack cannot drift on them; this file is
// only the Fluent DOM rendering. One deliberate departure from a former version of this pack:
// MaxColumns is a CAP (auto-fill with a percentage minimum — the Blazor CardGridStyle formula),
// never an exact `repeat(n, …)` count, which squeezed cards to slivers on narrow screens.

import type { CSSProperties, ReactNode } from "react";
import { useEffect, useMemo, useState } from "react";
import { Avatar, Badge, Button, Card, Input, Link, Spinner, Text } from "@fluentui/react-components";
import { Add20Regular, Search20Regular } from "@fluentui/react-icons";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshLink } from "../area/navigation.js";
import { useLocalize } from "../i18n/LocaleContext.js";
import { useMeshOps, type MeshOps } from "../live/meshOps.js";
import { str, useText } from "./common.js";
import { AddressAreaEmbed } from "./display.js";
import { iconForRendering } from "./iconValue.js";
import { InitialBubble, MeshIcon } from "./MeshIcon.js";
import {
  accessLogQuery,
  buildGroups,
  mergeUnionResults,
  paintOrdered,
  parseScopeTabs,
  parseSortOptions,
  toAccessOrder,
  toSearchResult,
  unionQueries,
  withRowOnlySelect,
  type MeshSearchResult,
  type MeshSearchScope,
} from "./meshSearchModel.js";

/** Debounced value — live-search keystrokes coalesce before hitting the mesh. */
function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

/**
 * Run one or more mesh queries through MeshOps.search — a newline-joined UNION issues each line
 * separately (the search verb takes ONE query per call), concatenating in declaration order and
 * deduping by path. Empty results when ops/search are absent.
 */
function useMeshQuery(
  ops: MeshOps | null,
  queries: string[],
  basePath?: string,
): { results: MeshSearchResult[]; loading: boolean } {
  const [state, setState] = useState<{ results: MeshSearchResult[]; loading: boolean }>({ results: [], loading: false });
  const key = queries.join("\n");
  useEffect(() => {
    if (!ops?.search || queries.length === 0) {
      setState({ results: [], loading: false });
      return;
    }
    let live = true;
    setState((s) => ({ ...s, loading: true }));
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
 * The viewer's own access log as a lookup {activityId → accessed-at ms} — ONE cheap
 * single-partition query, fetched only when a scope asked for access ordering and the host
 * exposes the viewer's id (ops.userId). Null until it lands, so the tiles paint from their own
 * query first and re-order when the log arrives (the ordering never gates the first paint).
 */
function useAccessOrder(ops: MeshOps | null, enabled: boolean): Map<string, number> | null {
  const viewer = enabled ? str(ops?.userId) : "";
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

// ---- MeshSearch ----------------------------------------------------------------------------------

/**
 * A per-item ITEM-AREA card — the Blazor MeshSearchView ItemArea mode: the item delegates its
 * rendering to a layout area hosted on the node's own hub (e.g. the home Pinned row's
 * PinnedThumbnail cards, which carry the unpin overlay). NO outer link: the embedded area's own
 * MeshNodeCard already navigates — wrapping it would nest <a> inside <a> (HTML splits those,
 * duplicating every card link).
 */
function ItemAreaCard({ node, itemArea }: { node: MeshSearchResult; itemArea: string }): ReactNode {
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

/** The node's list/card icon — its Icon field (SVG/URL/emoji), the thumbnail, or the initial
 *  bubble, exactly the fallback chain Blazor's MeshSearchView applies per row. */
function NodeResultIcon({ node, size }: { node: MeshSearchResult; size: number }): ReactNode {
  return (
    <MeshIcon
      value={iconForRendering(node.icon) ?? node.thumbnail ?? null}
      size={size}
      style={{ borderRadius: 6 }}
      fallback={<InitialBubble name={node.name} size={size} style={{ borderRadius: 6 }} />}
    />
  );
}

function ResultCard({ node, target }: { node: MeshSearchResult; target: string }): ReactNode {
  const link = useMeshLink(`/${target}`);
  return (
    <Link href={link.href} onClick={link.onClick} style={{ textDecoration: "none" }}>
      <Card style={{ padding: 12, gap: 4, height: "100%" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <NodeResultIcon node={node} size={24} />
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

// The Blazor search-result row: 40px node icon, name over description, type badge on the right.
function ResultRow({ node, target }: { node: MeshSearchResult; target: string }): ReactNode {
  const link = useMeshLink(`/${target}`);
  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        gap: 12,
        padding: "10px 4px",
        borderBottom: "1px solid var(--colorNeutralStroke3)",
      }}
    >
      <NodeResultIcon node={node} size={40} />
      <div style={{ display: "flex", flexDirection: "column", flex: 1, minWidth: 0, gap: 2 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, minWidth: 0 }}>
          <Link href={link.href} onClick={link.onClick} style={{ minWidth: 0 }}>
            <Text weight="semibold" style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
              {node.name}
            </Text>
          </Link>
          {node.nodeType ? (
            <Badge appearance="outline" size="small">
              {node.nodeType}
            </Badge>
          ) : null}
        </div>
        {node.description ? (
          <Text
            size={200}
            style={{
              color: "var(--colorNeutralForeground3)",
              overflow: "hidden",
              display: "-webkit-box",
              WebkitLineClamp: 2,
              WebkitBoxOrient: "vertical",
            }}
          >
            {node.description}
          </Text>
        ) : null}
      </div>
    </div>
  );
}

/**
 * One tile of the phone-home ICON grid (Icons mode): a large rounded icon with the name
 * underneath, navigating to the row's TARGET (its mainNode under NavigateToMainNode) — everything
 * comes from the query ROW (name/icon are result columns), never from the node's content or a
 * per-result hub. The Blazor mesh-search-icon-tile twin.
 */
function IconTile({ node, target }: { node: MeshSearchResult; target: string }): ReactNode {
  const link = useMeshLink(`/${target}`);
  return (
    <a
      href={link.href}
      onClick={link.onClick}
      title={node.name}
      style={{
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        gap: 8,
        padding: "8px 4px",
        borderRadius: 12,
        textDecoration: "none",
        color: "inherit",
      }}
    >
      <div
        style={{
          width: 64,
          height: 64,
          borderRadius: 16,
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          background: "var(--colorNeutralBackground3)",
          border: "1px solid var(--colorNeutralStroke2)",
          overflow: "hidden",
          flexShrink: 0,
        }}
      >
        <MeshIcon
          value={iconForRendering(node.icon) ?? node.thumbnail ?? null}
          size={64}
          style={{ borderRadius: 16, objectFit: "cover" }}
          fallback={<InitialBubble name={node.name} size={64} style={{ borderRadius: 16, fontSize: 26 }} />}
        />
      </div>
      <Text
        size={200}
        style={{
          textAlign: "center",
          maxWidth: "100%",
          overflow: "hidden",
          display: "-webkit-box",
          WebkitLineClamp: 2,
          WebkitBoxOrient: "vertical",
          wordBreak: "break-word",
          lineHeight: 1.25,
        }}
      >
        {node.name}
      </Text>
    </a>
  );
}

function num(v: unknown): number {
  const n = Math.trunc(Number(v));
  return Number.isFinite(n) ? n : 0;
}

export function MeshSearchView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const t = useLocalize();
  const title = useText(control.title);
  const controlHiddenQuery = str(useResolve(control.hiddenQuery));
  const initialVisible = str(useResolve(control.visibleQuery));
  const placeholder = str(useResolve(control.placeholder)) || t("common.typeToSearch");
  const ns = str(useResolve(control.namespace));
  const controlRenderMode = str(useResolve(control.renderMode)) || "Flat";
  const showSearchBox = useResolve(control.showSearchBox) !== false;
  const showViewOptions = useResolve(control.showViewOptions) === true;
  const liveSearch = useResolve(control.liveSearch) !== false;
  const excludeBasePath = useResolve(control.excludeBasePath) !== false;
  const showEmptyMessage = useResolve(control.showEmptyMessage) !== false;
  const showLoading = useResolve(control.showLoadingIndicator) !== false;
  const createHref = str(useResolve(control.createHref));
  const controlItemArea = str(useResolve(control.itemArea));
  const controlNavigateToMainNode = useResolve(control.navigateToMainNode) === true;
  const groupByFrequency = useResolve(control.groupByFrequency) === true;
  const maxColumns = num(useResolve(control.maxColumns));
  const minItemWidth = num(useResolve(control.minItemWidth));
  const grid = (control.grid ?? {}) as Record<string, unknown>;
  const gridSpacing = num(grid.spacing);
  const sections = (control.sections ?? {}) as Record<string, unknown>;
  const showCounts = sections.showCounts !== false;
  const collapsible = sections.collapsible !== false;
  const itemLimit = num(sections.itemLimit);
  const maxRows = num(sections.maxRows);
  const grouping = (control.grouping ?? {}) as Record<string, unknown>;

  // Scope tabs: the active tab is tracked by LABEL (the list is reactive — a bare index could
  // silently re-point at a different scope on a list change); a vanished label clamps to the
  // first tab. The strip renders only for 2+ tabs; a single tab still applies its settings.
  const scopeTabs = useMemo(() => parseScopeTabs(control.scopeTabs), [control.scopeTabs]);
  const [activeScopeLabel, setActiveScopeLabel] = useState<string | null>(null);
  const activeScopeIndex = Math.max(
    0,
    scopeTabs.findIndex((s) => s.label === activeScopeLabel),
  );
  const scope: MeshSearchScope | null = scopeTabs[activeScopeIndex] ?? null;

  // Sort-by dropdown: the scope's own options REPLACE the control-level set; the selection resets
  // to the scope default on a scope switch (SelectScope semantics). Each option carries a FULL
  // hidden query, so picking one swaps the base query.
  const controlSortOptions = useMemo(() => parseSortOptions(control.sortOptions), [control.sortOptions]);
  const sortOptions = scope?.sortOptions.length ? scope.sortOptions : controlSortOptions;
  const [sortLabel, setSortLabel] = useState<string | null>(null);
  const activeSort = (sortLabel && sortOptions.find((o) => o.label === sortLabel)) || null;

  const hiddenQuery = activeSort?.query ?? (scope ? scope.query : controlHiddenQuery);
  const renderMode = scope?.renderMode ?? controlRenderMode;
  const itemArea = scope?.itemArea ?? controlItemArea;
  const navigateToMainNode = scope?.navigateToMainNode ?? controlNavigateToMainNode;
  const sortByAccess = scope?.sortByAccess ?? false;
  const isIcons = renderMode === "Icons";
  const isList = renderMode === "List";
  const isGrouped = renderMode === "Grouped";

  const createLink = useMeshLink(createHref || undefined);
  const [visible, setVisible] = useState(initialVisible);
  const [submitted, setSubmitted] = useState(initialVisible);
  const term = useDebounced(liveSearch ? visible : submitted, 250);
  // The hidden query's newline-separated UNION legs each get the visible term appended; the icon
  // grid ships row-only (`select:` without content) unless the authored query already selects.
  const queries = unionQueries(hiddenQuery, term, (leg) => withRowOnlySelect(leg, isIcons));
  const { results, loading } = useMeshQuery(ops, queries, ns);

  const accessOrder = useAccessOrder(ops, sortByAccess);
  const targetOf = (n: MeshSearchResult) => (navigateToMainNode && n.mainNode ? n.mainNode : n.path);
  let items = excludeBasePath && ns ? results.filter((n) => n.path !== ns) : results;
  if (sortByAccess) items = paintOrdered(items, accessOrder, targetOf);

  const groupBy = str(grouping.groupByProperty) || "NodeType";
  const groups = buildGroups(items, isGrouped, groupBy, groupByFrequency);
  const skipHeaders = groups.length === 1;
  const [collapsedGroups, setCollapsedGroups] = useState<ReadonlySet<string>>(new Set());
  const [expandedGroups, setExpandedGroups] = useState<ReadonlySet<string>>(new Set());
  const toggleCollapsed = (key: string) =>
    setCollapsedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  const toggleExpanded = (key: string) =>
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  // MaxRows caps the visible items per group (rows × columns, Blazor GetMaxVisibleItems);
  // "Show all N" reveals the rest.
  const maxVisiblePerGroup = maxRows > 0 ? maxRows * (maxColumns > 0 ? maxColumns : 3) : 0;

  const selectScope = (index: number) => {
    if (index === activeScopeIndex || index < 0 || index >= scopeTabs.length) return;
    setActiveScopeLabel(scopeTabs[index].label);
    setSortLabel(null); // back to the scope's default (first) sort
    setCollapsedGroups(new Set());
    setExpandedGroups(new Set());
  };

  // The card grid: MaxColumns is a CAP via auto-fill with a percentage minimum (the Blazor
  // CardGridStyle formula), floored at MinItemWidth (200 default) so cards never squeeze to
  // slivers; unset → responsive auto-fill on the same floor.
  const floor = minItemWidth > 0 ? minItemWidth : 200;
  const gridStyle: CSSProperties = {
    display: "grid",
    gap: gridSpacing > 0 ? gridSpacing : 12,
    gridTemplateColumns:
      maxColumns === 1
        ? "1fr"
        : maxColumns > 1
          ? `repeat(auto-fill, minmax(max(${(100 / maxColumns).toFixed(1)}% - 8px, ${floor}px), 1fr))`
          : `repeat(auto-fill, minmax(${floor}px, 1fr))`,
  };
  // The phone-home icon grid (Blazor mesh-search-icons-grid): compact auto-fill tiles.
  const iconsGridStyle: CSSProperties = {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fill, minmax(96px, 1fr))",
    gap: "16px 8px",
    width: "100%",
    padding: "8px 0",
  };

  const renderItems = (groupItems: MeshSearchResult[]) =>
    isIcons ? (
      <div style={iconsGridStyle}>
        {groupItems.map((n) => (
          <IconTile key={n.path} node={n} target={targetOf(n)} />
        ))}
      </div>
    ) : isList ? (
      <div style={{ display: "flex", flexDirection: "column" }}>
        {groupItems.map((n) => (
          <ResultRow key={n.path} node={n} target={targetOf(n)} />
        ))}
      </div>
    ) : (
      <div style={gridStyle}>
        {groupItems.map((n) =>
          itemArea ? (
            <ItemAreaCard key={n.path} node={n} itemArea={itemArea} />
          ) : (
            <ResultCard key={n.path} node={n} target={targetOf(n)} />
          ),
        )}
      </div>
    );

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      {scopeTabs.length > 1 ? (
        <div
          role="tablist"
          style={{ display: "flex", gap: 4, borderBottom: "1px solid var(--colorNeutralStroke2)", overflowX: "auto" }}
        >
          {scopeTabs.map((tab, i) => (
            <button
              key={tab.label || String(i)}
              type="button"
              role="tab"
              aria-selected={i === activeScopeIndex}
              onClick={() => selectScope(i)}
              style={{
                background: "none",
                border: "none",
                borderBottom: i === activeScopeIndex ? "2px solid var(--colorBrandForeground1)" : "2px solid transparent",
                padding: "8px 12px",
                cursor: "pointer",
                fontSize: 14,
                whiteSpace: "nowrap",
                color: i === activeScopeIndex ? "var(--colorBrandForeground1)" : "inherit",
                fontWeight: i === activeScopeIndex ? 600 : 400,
              }}
            >
              {tab.label}
            </button>
          ))}
        </div>
      ) : null}
      <div style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
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
            <Button icon={<Add20Regular />} appearance="subtle" aria-label={t("search.createNew")} />
          </Link>
        ) : null}
        {showViewOptions && sortOptions.length > 0 ? (
          <label style={{ display: "inline-flex", alignItems: "center", gap: 6, marginLeft: "auto" }}>
            <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
              {t("search.sortBy")}
            </Text>
            <select
              value={activeSort?.label ?? sortOptions[0].label}
              onChange={(e) => setSortLabel(e.target.value)}
              style={{ fontSize: 13, padding: "2px 4px" }}
            >
              {sortOptions.map((o) => (
                <option key={o.label} value={o.label}>
                  {o.label}
                </option>
              ))}
            </select>
          </label>
        ) : null}
      </div>
      {loading && showLoading ? <Spinner size="tiny" label={t("common.searching")} /> : null}
      {!loading && items.length === 0 && showEmptyMessage && queries.length > 0 ? (
        <Text italic size={200}>
          {t("empty.noItemsFound")}
        </Text>
      ) : null}
      {groups.map((group) => {
        const isCollapsed = collapsedGroups.has(group.key);
        const isExpanded = expandedGroups.has(group.key);
        // ItemLimit bounds what a section LOADS (per group, like Blazor's Sections.ItemLimit);
        // MaxRows caps what it SHOWS until "Show all". The header count stays the full count.
        const loaded = itemLimit > 0 && group.items.length > itemLimit ? group.items.slice(0, itemLimit) : group.items;
        const capped = maxVisiblePerGroup > 0 && !isExpanded && loaded.length > maxVisiblePerGroup;
        const visibleItems = capped ? loaded.slice(0, maxVisiblePerGroup) : loaded;
        const headerLabel = showCounts ? `${group.label} (${group.items.length})` : group.label;
        return (
          <div key={group.key || "·"} style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            {!skipHeaders ? (
              collapsible ? (
                <button
                  type="button"
                  onClick={() => toggleCollapsed(group.key)}
                  style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 6,
                    background: "none",
                    border: "none",
                    padding: "4px 0",
                    cursor: "pointer",
                    color: "inherit",
                  }}
                >
                  <span
                    aria-hidden
                    style={{
                      display: "inline-block",
                      fontSize: 10,
                      transform: isCollapsed ? "none" : "rotate(90deg)",
                      transition: "transform 0.1s",
                    }}
                  >
                    &#x25b6;
                  </span>
                  <Text weight="semibold">{headerLabel}</Text>
                </button>
              ) : (
                <Text weight="semibold" size={400}>
                  {headerLabel}
                </Text>
              )
            ) : null}
            {!isCollapsed ? (
              <>
                {renderItems(visibleItems)}
                {capped || (isExpanded && maxVisiblePerGroup > 0 && loaded.length > maxVisiblePerGroup) ? (
                  <div>
                    <Button appearance="subtle" size="small" onClick={() => toggleExpanded(group.key)}>
                      {capped ? t("search.showAllCount", loaded.length) : t("common.showLess")}
                    </Button>
                  </div>
                ) : null}
              </>
            ) : null}
          </div>
        );
      })}
    </div>
  );
}

// ---- MeshNodeCollection ---------------------------------------------------------------------------

export function MeshNodeCollectionView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const queries = (Array.isArray(control.queries) ? control.queries : []).map(str).filter(Boolean);
  const showAdd = control.showAdd !== false;
  const [items, setItems] = useState<MeshSearchResult[] | null>(null);

  useEffect(() => {
    if (!ops?.search || queries.length === 0) {
      setItems([]);
      return;
    }
    let live = true;
    Promise.all(queries.map((q) => ops.search!(q).catch(() => [] as Record<string, unknown>[])))
      .then((all) => {
        if (!live) return;
        const merged = new Map<string, MeshSearchResult>();
        for (const n of all.flat().map(toSearchResult)) if (n.path) merged.set(n.path, n);
        setItems([...merged.values()]);
      })
      .catch(() => {
        if (live) setItems([]);
      });
    return () => {
      live = false;
    };
    // JSON.stringify: an unambiguous identity for the query LIST — a bare join can collide
    // (["a","bc"] vs ["ab","c"]) and leave stale results on a list change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, JSON.stringify(queries)]);

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

function CollectionRow({ node }: { node: MeshSearchResult }): ReactNode {
  const link = useMeshLink(`/${node.path}`);
  return (
    <Link href={link.href} onClick={link.onClick} style={{ textDecoration: "none" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "6px 8px", borderRadius: 6 }}>
        <MeshIcon
          value={iconForRendering(node.icon) ?? node.thumbnail ?? null}
          size={28}
          style={{ borderRadius: "50%", overflow: "hidden" }}
          fallback={<Avatar name={node.name} size={28} />}
        />
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
