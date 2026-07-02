"use client";

// Client half of the /search page: a MeshSearch control tree over the live ops surface — the
// exact control Search.razor builds (visible query editable in the box, hidden query always
// appended, namespace scope, list render), rendered through the registry's MeshSearchView.

import { useMemo } from "react";
import { Text } from "@fluentui/react-components";
import { MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import type { AreaTree, UiControl } from "@meshweaver/react";
import { useLiveConnection } from "../../src/client/LiveConnection";
import { useHydratedTheme } from "../../src/client/useHydratedTheme";

export interface SearchResultsProps {
  query: string;
  hiddenQuery: string;
  namespace: string;
  limit: number;
}

export function SearchResults({ query, hiddenQuery, namespace, limit }: SearchResultsProps) {
  const live = useLiveConnection();
  const { theme } = useHydratedTheme();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;

  const source = useMemo(() => {
    const control: UiControl = {
      $type: "MeshSearch",
      visibleQuery: query,
      placeholder: "Search... (e.g. nodeType:Thread, namespace:ACME scope:descendants)",
      renderMode: "List",
      itemLimit: limit,
    };
    if (hiddenQuery) control.hiddenQuery = hiddenQuery;
    if (namespace) control.namespace = namespace;
    const tree: AreaTree = { areas: { "": control } };
    return new StaticAreaSource(tree);
  }, [query, hiddenQuery, namespace, limit]);

  return (
    <div style={{ maxWidth: 1400, margin: "0 auto", padding: 24, width: "100%" }}>
      {mesh ? (
        <MeshAreaView source={source} rootArea="" theme={theme} ops={mesh.ops} />
      ) : (
        <Text size={300} style={{ color: "var(--colorNeutralForeground3)" }}>
          {live.state.kind === "offline" ? "No live mesh connection — search is unavailable." : "Connecting to the mesh…"}
        </Text>
      )}
    </div>
  );
}
