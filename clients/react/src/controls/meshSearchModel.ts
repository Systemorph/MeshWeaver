// Platform-neutral MeshSearch model — the pure half of the Blazor MeshSearchView port, shared by
// the web pack (controls/meshLive.tsx) and the React-Native pack (rnMeshLive.tsx) so the two
// cannot drift on the home-design semantics:
//
//   - SCOPE TABS (MeshSearchScopeTab): the strip renders only for two or more tabs; ONE tab
//     renders no strip but still applies its settings (renderMode / itemArea / navigateToMainNode
//     / sortByAccess) — the home's Apps band carries Icons + SortByAccess on a single scope.
//   - UNION QUERIES: a hidden query can be a newline-joined UNION of sub-queries (the server
//     declares the home catalog that way); the search verb takes ONE query per call, so each line
//     is issued separately and the batches merge in declaration order, deduped by path.
//   - ICONS row-only select: the icon grid paints from query rows (name/icon/mainNode are result
//     columns), so the content column never belongs on the wire.
//   - SortByAccess: most-recently-used first, computed at PAINT from the viewer's own
//     `{viewer}/_UserActivity` satellites — the activity id is the visited path with '/' → '_',
//     so a tile's TARGET is mangled forward and looked up (the mangling is never reversed).
//     Never `source:accessed` in the query: that INNER JOIN hides every never-opened app.
//   - Grouped mode: sections by NodeType (or grouping.groupByProperty) with counts, biggest
//     group first under GroupByFrequency (ties by label, stable).
//
// No React, no DOM, no Fluent — everything here is directly unit-testable.

function str(v: unknown): string {
  return v == null ? "" : String(v);
}

/** One search result row — the subset of the wire MeshNode both packs paint from. */
export interface MeshSearchResult {
  path: string;
  /** The row's id — the access log's UserActivity rows key their visit by it. */
  id: string;
  name: string;
  nodeType: string;
  description: string;
  /** The node this row REPRESENTS (an app record's app) — the NavigateToMainNode target. */
  mainNode: string;
  /** The node's raw icon value (inline SVG / URL / emoji / name) — packs classify it themselves. */
  icon: string;
  thumbnail?: string;
  lastModified?: string;
}

/** Project a wire MeshNode row into the shared result shape (name falls back to the path leaf). */
export function toSearchResult(r: Record<string, unknown>): MeshSearchResult {
  const content = (r.content ?? {}) as Record<string, unknown>;
  return {
    path: str(r.path),
    id: str(r.id),
    name: str(r.name) || str(r.path).split("/").pop() || str(r.path),
    nodeType: str(r.nodeType),
    description: str(r.description ?? content.description),
    mainNode: str(r.mainNode),
    icon: str(r.icon ?? content.icon),
    thumbnail: str(content.thumbnailUrl ?? content.imageUrl) || undefined,
    lastModified: str(r.lastModified) || undefined,
  };
}

/** One user-selectable sort choice (MeshSearchSortOption): picking it swaps the FULL hidden query. */
export interface MeshSearchSortOption {
  label: string;
  query: string;
}

/** One scope tab (MeshSearchScopeTab wire shape, camelCase). */
export interface MeshSearchScope {
  label: string;
  query: string;
  sortOptions: MeshSearchSortOption[];
  itemArea?: string;
  renderMode?: string;
  navigateToMainNode?: boolean;
  sortByAccess: boolean;
}

/** Parse the control's `sortOptions` wire value (unknown-shaped JSON in, typed list out). */
export function parseSortOptions(raw: unknown): MeshSearchSortOption[] {
  if (!Array.isArray(raw)) return [];
  return raw
    .map((o) => {
      const r = (o ?? {}) as Record<string, unknown>;
      return { label: str(r.label), query: str(r.query) };
    })
    .filter((o) => o.label.length > 0);
}

/** Parse the control's `scopeTabs` wire value. Serializer default-suppression means a false
 *  bool arrives ABSENT — only an explicit boolean is carried through, so the pack-level
 *  fallback (the control-level setting) still applies when a scope says nothing. */
export function parseScopeTabs(raw: unknown): MeshSearchScope[] {
  if (!Array.isArray(raw)) return [];
  return raw
    .map((t) => {
      const r = (t ?? {}) as Record<string, unknown>;
      return {
        label: str(r.label),
        query: str(r.query),
        sortOptions: parseSortOptions(r.sortOptions),
        itemArea: str(r.itemArea) || undefined,
        renderMode: str(r.renderMode) || undefined,
        navigateToMainNode: typeof r.navigateToMainNode === "boolean" ? r.navigateToMainNode : undefined,
        sortByAccess: r.sortByAccess === true,
      };
    })
    .filter((t) => t.label.length > 0 || t.query.length > 0);
}

/** Split a (possibly newline-joined UNION) hidden query into the individual queries to issue,
 *  appending the user's typed term to EACH leg. At least one query unless everything is empty. */
export function unionQueries(hiddenQuery: string, term: string, mapLeg: (leg: string) => string = (l) => l): string[] {
  const lines = hiddenQuery
    .split("\n")
    .map((l) => l.trim())
    .filter(Boolean);
  return (lines.length ? lines : [""]).map((l) => [mapLeg(l), term.trim()].filter(Boolean).join(" ")).filter(Boolean);
}

/** Merge per-leg result batches in declaration order, deduping by path. */
export function mergeUnionResults(batches: Record<string, unknown>[][]): MeshSearchResult[] {
  const seen = new Set<string>();
  const merged: MeshSearchResult[] = [];
  for (const rows of batches)
    for (const n of rows.map(toSearchResult))
      if (n.path.length > 0 && !seen.has(n.path)) {
        seen.add(n.path);
        merged.push(n);
      }
  return merged;
}

/** Row-only select for the icon grid — applied only when the authored query has no select: of
 *  its own (the server's RowOnlySelected guard). */
export function withRowOnlySelect(query: string, iconsMode: boolean): string {
  return iconsMode && !/select:/i.test(query)
    ? `${query} select:path,id,namespace,name,nodeType,icon,mainNode`.trim()
    : query;
}

/** The activity id the access log stores a visit to `path` under ('/' → '_', one-way). */
export function accessKeyOf(path: string): string {
  return path.replace(/\//g, "_");
}

/** The one cheap single-partition query for the viewer's access log (row-only, newest first). */
export function accessLogQuery(viewerId: string): string {
  return (
    `namespace:${viewerId}/_UserActivity nodeType:UserActivity ` +
    "select:path,id,namespace,name,nodeType,lastModified sort:LastModified-desc limit:500"
  );
}

/** Fold access-log rows into the {activityId → accessed-at ms} lookup (first hit wins — the rows
 *  arrive newest first). */
export function toAccessOrder(rows: Record<string, unknown>[]): Map<string, number> {
  const m = new Map<string, number>();
  for (const r of rows) {
    const id = str(r.id);
    const at = Date.parse(str(r.lastModified));
    if (id && Number.isFinite(at) && !m.has(id)) m.set(id, at);
  }
  return m;
}

/**
 * Results in paint order: most-recently-opened first, with never-opened results keeping the
 * query's own order BEHIND them (never dropped — that is exactly what a `source:accessed` INNER
 * JOIN would have done to a freshly installed app). A stable sort, so the query's order survives
 * inside each band.
 */
export function paintOrdered(
  items: MeshSearchResult[],
  accessOrder: Map<string, number> | null,
  targetOf: (n: MeshSearchResult) => string,
): MeshSearchResult[] {
  if (!accessOrder || accessOrder.size === 0 || items.length < 2) return items;
  return [...items].sort(
    (a, b) => (accessOrder.get(accessKeyOf(targetOf(b))) ?? 0) - (accessOrder.get(accessKeyOf(targetOf(a))) ?? 0),
  );
}

/** One rendered section of a grouped result set. */
export interface MeshSearchGroup {
  key: string;
  label: string;
  items: MeshSearchResult[];
}

function groupValueOf(n: MeshSearchResult, property: string): string {
  const p = property.toLowerCase();
  if (p === "nodetype") return n.nodeType;
  if (p === "name") return n.name;
  const r = n as unknown as Record<string, unknown>;
  return str(r[property] ?? r[property.charAt(0).toLowerCase() + property.slice(1)]);
}

/**
 * Bucket results by the group-by property (NodeType default), biggest group first when
 * `byFrequency` (ties by label, so the order is stable across renders) — the home's content
 * section fans out by the node's own type with the type you have most of at the top. Non-grouped
 * modes yield ONE unlabeled group, which the packs render without a header.
 */
export function buildGroups(
  items: MeshSearchResult[],
  grouped: boolean,
  groupBy: string,
  byFrequency: boolean,
): MeshSearchGroup[] {
  if (!grouped) return [{ key: "", label: "", items }];
  const buckets = new Map<string, MeshSearchResult[]>();
  for (const n of items) {
    const key = groupValueOf(n, groupBy) || n.nodeType.split("/").pop() || "";
    const bucket = buckets.get(key);
    if (bucket) bucket.push(n);
    else buckets.set(key, [n]);
  }
  const groups = [...buckets.entries()].map(([key, groupItems]) => ({
    key,
    label: key || groupItems[0]?.nodeType.split("/").pop() || "Items",
    items: groupItems,
  }));
  groups.sort((a, b) =>
    byFrequency ? b.items.length - a.items.length || a.label.localeCompare(b.label) : a.label.localeCompare(b.label),
  );
  return groups;
}
