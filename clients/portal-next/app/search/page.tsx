// The search results page — the React port of the memex portal's /search
// (memex/Memex.Portal.Shared/Pages/Search.razor): the same query parameters (q = visible/editable
// query, hq = hidden query parts (repeatable), ns = namespace scope, limit) rendered through the
// SAME MeshSearch control the mesh streams — the client component builds the control tree and the
// registry's MeshSearchView runs the live query through the session's ops surface.

import { SearchResults } from "./SearchResults";

export const dynamic = "force-dynamic";

interface SearchParams {
  q?: string | string[];
  hq?: string | string[];
  ns?: string | string[];
  limit?: string | string[];
}

function first(v: string | string[] | undefined): string {
  return Array.isArray(v) ? (v[0] ?? "") : (v ?? "");
}

function all(v: string | string[] | undefined): string[] {
  return Array.isArray(v) ? v : v ? [v] : [];
}

export default function SearchPage({ searchParams }: { searchParams: SearchParams }) {
  const limitRaw = Number.parseInt(first(searchParams.limit), 10);
  return (
    <SearchResults
      query={first(searchParams.q)}
      hiddenQuery={all(searchParams.hq).filter(Boolean).join(" ")}
      namespace={first(searchParams.ns)}
      limit={Number.isFinite(limitRaw) && limitRaw > 0 ? limitRaw : 50}
    />
  );
}
