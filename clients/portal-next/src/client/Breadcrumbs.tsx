"use client";

// The slim breadcrumb bar under the header — the React port of the Blazor shell's breadcrumb row
// (⌂ Home › segment › segment), fed by the navigation state the routed LiveArea publishes.
// Each crumb links to its cumulative mesh path; the last segment is the current page (plain text).
// Crumbs display the node's NAME (resolved via the one-shot query-nodes REST read, cached for the
// shell's lifetime), falling back to the decoded path segment for prefixes that aren't nodes
// (area segments like "Settings") or while the name is still resolving.

import { useEffect, useRef, useState, type ReactNode } from "react";
import Link from "next/link";
import { Home16Regular } from "@fluentui/react-icons";
import { useLiveConnection, useNavigationState } from "./LiveConnection";

function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}

export function Breadcrumbs(): ReactNode {
  const { path } = useNavigationState();
  const { state } = useLiveConnection();
  const mesh = state.kind === "live" ? state.mesh : null;
  const segments = (path ?? "").split("/").filter(Boolean);
  // Resolved node names keyed by decoded mesh path. The component lives in the persistent shell,
  // so the cache spans navigations; `requested` dedupes in-flight lookups (a prefix that resolves
  // to no node is requested once and simply keeps its segment fallback).
  const [names, setNames] = useState<Record<string, string>>({});
  const requested = useRef(new Set<string>());

  useEffect(() => {
    if (!mesh || segments.length === 0) return;
    const prefixes = segments.map((_, i) =>
      segments.slice(0, i + 1).map(decodeURIComponent).join("/"),
    );
    for (const prefix of prefixes) {
      if (requested.current.has(prefix)) continue;
      requested.current.add(prefix);
      // One-shot REST read (returns [] on any failure) — no live stream to hold for chrome.
      void mesh.queryNodes(`path:${prefix} scope:self limit:1`, 1).then((rows) => {
        const name = rows.length > 0 ? str(rows[0].name) : "";
        if (name) setNames((prev) => ({ ...prev, [prefix]: name }));
      });
    }
    // segments derives from path; path is the effect's real input.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mesh, path]);

  return (
    <nav
      aria-label="Breadcrumb"
      data-mw-breadcrumbs
      style={{
        display: "flex",
        alignItems: "center",
        gap: 6,
        padding: "0 16px",
        height: 32,
        borderBottom: "1px solid var(--colorNeutralStroke2)",
        fontSize: 13,
        whiteSpace: "nowrap",
        overflow: "hidden",
      }}
    >
      <Link
        href="/"
        style={{
          display: "inline-flex",
          alignItems: "center",
          gap: 4,
          color: "var(--colorBrandForeground1)",
          textDecoration: "none",
          flexShrink: 0,
        }}
      >
        <Home16Regular />
        Home
      </Link>
      {segments.map((seg, i) => {
        const last = i === segments.length - 1;
        const href = "/" + segments.slice(0, i + 1).join("/");
        const prefix = segments
          .slice(0, i + 1)
          .map(decodeURIComponent)
          .join("/");
        const name = names[prefix] || decodeURIComponent(seg);
        return (
          <span key={href} style={{ display: "inline-flex", alignItems: "center", gap: 6, minWidth: 0 }}>
            <span aria-hidden style={{ color: "var(--colorNeutralForeground3)" }}>
              ›
            </span>
            {last ? (
              <span style={{ color: "var(--colorNeutralForeground1)", overflow: "hidden", textOverflow: "ellipsis" }}>
                {name}
              </span>
            ) : (
              <Link href={href} style={{ color: "var(--colorBrandForeground1)", textDecoration: "none" }}>
                {name}
              </Link>
            )}
          </span>
        );
      })}
    </nav>
  );
}
