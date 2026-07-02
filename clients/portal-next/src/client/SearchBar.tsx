"use client";

// The portal's global mesh search bar — the React port of the Blazor shell's SearchBar
// (src/MeshWeaver.Blazor.Portal/Components/SearchBar.razor + .razor.cs) and its MeshSearch
// suggestion composer (Components/MeshSearch.cs). Behavior pinned against the Blazor source:
//
//   - 250ms keystroke debounce; suggestions capped at 10.
//   - Empty box on focus → the recently-accessed default set
//     ("source:accessed scope:descendants is:main sort:LastModified-desc context:search limit:10").
//   - "@path" → path autocomplete (the wire AutocompleteRequest, RelevanceFirst server-side).
//   - Free text → substring candidate pool ("*{t}* scope:descendants context:search is:main
//     limit:50") re-scored client-side by match quality + path proximity (the exact
//     ComputeRelevanceScore + PathProximity.ComputeBoost tiers).
//   - "/" focuses the bar globally unless an editor/input has focus; ArrowUp/Down cycle;
//     Enter navigates the highlighted suggestion or submits; Escape closes.
//   - Submit: "@path" → /{path}; "@path query" → /search?q=namespace:{path} scope:descendants {query};
//     plain text → /search?q={query}&hq=scope:descendants.
//
// One deliberate divergence from Blazor: the bar fills ALL remaining header space (flex:1),
// not a fixed narrow box.

import { useCallback, useEffect, useRef, useState } from "react";
import type { KeyboardEvent as ReactKeyboardEvent } from "react";
import { useRouter } from "next/navigation";
import { Input, Text } from "@fluentui/react-components";
import { Search20Regular } from "@fluentui/react-icons";
import type { AutocompleteRow, LiveMesh, MeshNodeRow } from "./live";
import { useLiveConnection, useNavigationState } from "./LiveConnection";
import { NodeIcon, nodeTypeDisplay } from "./icons";

const SEARCH_PLACEHOLDER = "Search the mesh... (e.g. nodeType:Story status:Open)";
const MAX_RESULTS = 10;
const CANDIDATE_POOL_SIZE = 50;
const DEBOUNCE_MS = 250;
const BLUR_CLOSE_DELAY_MS = 200;

interface QuerySuggestion {
  path: string;
  name: string;
  nodeType?: string;
  icon?: string;
  score: number;
}

// ---- suggestion composition (MeshSearch.cs port) --------------------------------------------

function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}

function toSuggestion(row: MeshNodeRow): QuerySuggestion {
  const path = str(row.path);
  return {
    path,
    name: str(row.name) || str(row.id) || path.split("/").pop() || path,
    nodeType: str(row.nodeType) || undefined,
    icon: str(row.icon) || undefined,
    score: 0,
  };
}

function fromAutocomplete(item: AutocompleteRow): QuerySuggestion | null {
  const path = str(item.path) || str(item.insertText).replace(/^@\/?/, "");
  if (!path) return null;
  return { path, name: str(item.label) || path.split("/").pop() || path, icon: str(item.icon) || undefined, score: 0 };
}

/** PathProximity.ComputeBoost — MaxBoost/(1+segmentDistance), 0 without context. */
export function proximityBoost(contextPath: string | null | undefined, resultPath: string | null | undefined): number {
  if (!contextPath) return 0;
  const a = contextPath.split("/").filter(Boolean);
  const b = (resultPath ?? "").split("/").filter(Boolean);
  let lcp = 0;
  while (lcp < Math.min(a.length, b.length) && a[lcp].toLowerCase() === b[lcp].toLowerCase()) lcp++;
  const distance = a.length - lcp + (b.length - lcp);
  return 40 / (1 + distance);
}

/** MeshSearch.ComputeRelevanceScore — name-match tiers + proximity, normalized per term. */
export function relevanceScore(
  row: { name: string; path: string; nodeType?: string },
  input: string,
  contextPath: string | null | undefined,
): number {
  const terms = input.split(" ").filter(Boolean);
  let total = 0;
  let scored = 0;
  const name = row.name.toLowerCase();
  const path = row.path.toLowerCase();
  const nodeType = (row.nodeType ?? "").toLowerCase();
  for (const rawTerm of terms) {
    const term = rawTerm.replace(/^\*+|\*+$/g, "").toLowerCase();
    if (!term) continue;
    scored++;
    if (name.startsWith(term)) total += 100;
    else if (name.includes(term)) total += 80;
    else if (path.includes(term)) total += 20;
    else if (nodeType.includes(term)) total += 10;
    else total += 1; // matched in content/description
  }
  const score = scored > 0 ? total / scored : 1;
  return score + proximityBoost(contextPath, row.path);
}

/** The suggestion set for one input state — empty → recent, @path → autocomplete, text → scored. */
async function fetchSuggestions(
  mesh: LiveMesh,
  input: string | null,
  contextPath: string | null,
): Promise<QuerySuggestion[]> {
  const trimmed = input?.trim() ?? "";

  if (!trimmed) {
    const rows = await mesh.queryNodes(
      `source:accessed scope:descendants is:main sort:LastModified-desc context:search limit:${MAX_RESULTS}`,
      MAX_RESULTS,
    );
    return rows.map(toSuggestion).filter((s) => s.path).slice(0, MAX_RESULTS);
  }

  if (trimmed.startsWith("@")) {
    const items = await mesh.autocomplete(trimmed, contextPath ?? undefined);
    return items
      .map(fromAutocomplete)
      .filter((s): s is QuerySuggestion => s != null)
      .slice(0, MAX_RESULTS);
  }

  const rows = await mesh.queryNodes(
    `*${trimmed}* scope:descendants context:search is:main limit:${CANDIDATE_POOL_SIZE}`,
    CANDIDATE_POOL_SIZE,
  );
  return rows
    .map(toSuggestion)
    .filter((s) => s.path)
    .map((s) => ({ ...s, score: relevanceScore(s, trimmed, contextPath) }))
    .sort((a, b) => b.score - a.score)
    .slice(0, MAX_RESULTS);
}

// ---- the component ---------------------------------------------------------------------------

export function SearchBar() {
  const router = useRouter();
  const live = useLiveConnection();
  const nav = useNavigationState();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;
  const contextPath = nav.target?.address || nav.path || null;

  const inputRef = useRef<HTMLInputElement>(null);
  const [value, setValue] = useState("");
  const [suggestions, setSuggestions] = useState<QuerySuggestion[]>([]);
  const [showDropdown, setShowDropdown] = useState(false);
  const [highlighted, setHighlighted] = useState(-1);
  const [isLoading, setIsLoading] = useState(false);

  // One in-flight generation counter — a newer keystroke's results always win (the Switch()
  // semantics of the Blazor pipeline, without binding stale results).
  const generation = useRef(0);
  const runSearch = useCallback(
    (term: string | null) => {
      if (!mesh) return;
      const gen = ++generation.current;
      setIsLoading(true);
      fetchSuggestions(mesh, term, contextPath).then(
        (result) => {
          if (generation.current !== gen) return;
          setSuggestions(result);
          setIsLoading(false);
        },
        () => {
          if (generation.current !== gen) return;
          setSuggestions([]);
          setIsLoading(false);
        },
      );
    },
    [mesh, contextPath],
  );

  // Keystroke debounce (250ms — SearchBar.razor.cs Throttle).
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const onInput = (next: string) => {
    setValue(next);
    setHighlighted(-1);
    if (!next.trim()) {
      generation.current++; // invalidate any in-flight fetch — a late result must not repopulate
      setSuggestions([]);
      setShowDropdown(false);
      setIsLoading(false);
      if (debounceTimer.current) clearTimeout(debounceTimer.current);
      return;
    }
    setIsLoading(true);
    setShowDropdown(true);
    if (debounceTimer.current) clearTimeout(debounceTimer.current);
    debounceTimer.current = setTimeout(() => runSearch(next.trim()), DEBOUNCE_MS);
  };

  // Global "/" shortcut — focus the bar unless an editor/input owns the keyboard.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "/" || e.ctrlKey || e.metaKey || e.altKey) return;
      const el = document.activeElement as HTMLElement | null;
      if (el) {
        const tag = el.tagName;
        if (tag === "TEXTAREA") return;
        if (tag === "INPUT" && /^(text|search|url|email|tel|password)$/i.test((el as HTMLInputElement).type)) return;
        if (el.isContentEditable) return;
        if (el.closest(".monaco-editor")) return;
      }
      e.preventDefault();
      inputRef.current?.focus();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const clearSearch = () => {
    generation.current++; // invalidate any in-flight fetch — a late result must not repopulate
    setValue("");
    setSuggestions([]);
    setShowDropdown(false);
    setHighlighted(-1);
    setIsLoading(false);
    inputRef.current?.blur();
  };

  const navigateTo = (suggestion: QuerySuggestion) => {
    router.push(`/${suggestion.path}`);
    clearSearch();
  };

  // Query submission — the exact routing split of SearchBar.HandleSubmit.
  const submit = () => {
    const trimmed = value.trim();
    if (!trimmed) return;
    if (trimmed.startsWith("@")) {
      const afterAt = trimmed.slice(1);
      const spaceIndex = afterAt.indexOf(" ");
      if (spaceIndex < 0) {
        const path = afterAt.replace(/\/+$/, "");
        if (path) {
          router.push(`/${path}`);
          clearSearch();
          return;
        }
      } else {
        const path = afterAt.slice(0, spaceIndex).replace(/\/+$/, "");
        const query = afterAt.slice(spaceIndex + 1).trim();
        if (path && query) {
          router.push(`/search?q=${encodeURIComponent(`namespace:${path} scope:descendants ${query}`)}`);
          clearSearch();
          return;
        }
      }
    }
    router.push(`/search?q=${encodeURIComponent(trimmed)}&hq=${encodeURIComponent("scope:descendants")}`);
    clearSearch();
  };

  const onKeyDown = (e: ReactKeyboardEvent<HTMLInputElement>) => {
    switch (e.key) {
      case "ArrowDown":
        if (suggestions.length > 0) {
          e.preventDefault();
          setShowDropdown(true);
          setHighlighted((h) => (h < 0 ? 0 : (h + 1) % suggestions.length));
        }
        break;
      case "ArrowUp":
        if (suggestions.length > 0) {
          e.preventDefault();
          setShowDropdown(true);
          setHighlighted((h) => (h <= 0 ? suggestions.length - 1 : h - 1));
        }
        break;
      case "Enter":
        if (highlighted >= 0 && highlighted < suggestions.length) navigateTo(suggestions[highlighted]);
        else submit();
        break;
      case "Escape":
        setShowDropdown(false);
        setHighlighted(-1);
        break;
    }
  };

  const onFocus = () => {
    if (suggestions.length > 0) {
      setShowDropdown(true);
      return;
    }
    if (!value.trim()) {
      // Empty box on focus → the recently-accessed default set.
      setIsLoading(true);
      setShowDropdown(true);
      runSearch(null);
    }
  };

  const blurTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const onBlur = () => {
    blurTimer.current = setTimeout(() => {
      setShowDropdown(false);
      setHighlighted(-1);
    }, BLUR_CLOSE_DELAY_MS);
  };
  useEffect(
    () => () => {
      if (blurTimer.current) clearTimeout(blurTimer.current);
      if (debounceTimer.current) clearTimeout(debounceTimer.current);
    },
    [],
  );

  const dropdownVisible = showDropdown && (isLoading || suggestions.length > 0);

  return (
    <div data-mw-searchbar style={{ position: "relative", flex: 1, minWidth: 0 }}>
      <Input
        ref={inputRef}
        contentBefore={<Search20Regular />}
        placeholder={SEARCH_PLACEHOLDER}
        value={value}
        autoComplete="off"
        appearance="filled-lighter"
        style={{ width: "100%" }}
        onChange={(_, d) => onInput(d.value)}
        onKeyDown={onKeyDown}
        onFocus={onFocus}
        onBlur={onBlur}
      />
      {dropdownVisible && (
        <div
          style={{
            position: "absolute",
            top: "calc(100% + 4px)",
            left: 0,
            right: 0,
            zIndex: 400,
            background: "var(--colorNeutralBackground1)",
            border: "1px solid var(--colorNeutralStroke2)",
            borderRadius: 8,
            boxShadow: "var(--shadow16)",
            overflow: "hidden",
            maxHeight: "70vh",
            overflowY: "auto",
          }}
        >
          {isLoading && suggestions.length === 0 ? (
            <div style={{ padding: "10px 12px", color: "var(--colorNeutralForeground3)" }}>Searching...</div>
          ) : (
            suggestions.map((s, index) => (
              <div
                key={`${s.path}-${index}`}
                onMouseDown={(e) => {
                  e.preventDefault();
                  navigateTo(s);
                }}
                style={{
                  display: "flex",
                  alignItems: "center",
                  gap: 10,
                  padding: "8px 12px",
                  cursor: "pointer",
                  background: index === highlighted ? "var(--colorNeutralBackground1Selected)" : "transparent",
                }}
                onMouseEnter={() => setHighlighted(index)}
              >
                <NodeIcon icon={s.icon} name={s.name} size={24} />
                <div style={{ minWidth: 0 }}>
                  <div style={{ fontWeight: 600, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                    {s.name}
                  </div>
                  <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
                    {s.path}
                    {s.nodeType ? ` · ${nodeTypeDisplay(s.nodeType)}` : ""}
                  </Text>
                </div>
              </div>
            ))
          )}
          {isLoading && suggestions.length > 0 && (
            <div
              data-mw-search-progress
              style={{
                height: 2,
                background:
                  "linear-gradient(90deg, transparent, var(--colorBrandBackground), transparent)",
                backgroundSize: "200% 100%",
                animation: "mw-search-progress 1s linear infinite",
              }}
            />
          )}
        </div>
      )}
      <style>{`@keyframes mw-search-progress { from { background-position: 200% 0; } to { background-position: -200% 0; } }`}</style>
    </div>
  );
}
