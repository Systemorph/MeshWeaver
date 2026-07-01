"use client";

// The portal chrome (header + nav) around the routed mesh area — ported from the Vite SPA's
// Portal.tsx, with Next's router in place of the hash route: nav items are <Link>s and the active
// item derives from usePathname(). The chrome lives in the App Router layout, so it renders in the
// FIRST HTML flush and persists across navigations while pages stream in below it.

import { useMemo, type ReactNode } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { Avatar, Button, FluentProvider, Input, Text, Tooltip } from "@fluentui/react-components";
import {
  ArrowHookUpLeft20Regular,
  Board20Regular,
  Book20Regular,
  Search20Regular,
} from "@fluentui/react-icons";
import { ThemeToggle } from "@meshweaver/react";
import { hrefForMeshPath } from "../meshPath";
import { useLiveConnection } from "./LiveConnection";
import { useHydratedTheme } from "./useHydratedTheme";

const nav = [
  { path: "", label: "Home", icon: <Board20Regular /> },
  { path: "Doc", label: "Documentation", icon: <Book20Regular /> },
];

export function PortalShell({ children }: { children: ReactNode }) {
  const { theme, mounted } = useHydratedTheme();
  const live = useLiveConnection();
  const pathname = usePathname() ?? "/";
  const currentPath = useMemo(() => decodeURIComponent(pathname).replace(/^\/+|\/+$/g, ""), [pathname]);
  const userName = live.state.kind === "live" ? live.state.mesh.userId : "Guest";

  return (
    <FluentProvider theme={theme} style={{ height: "100vh" }}>
      <div style={{ display: "grid", gridTemplateRows: "52px 1fr", height: "100vh" }}>
        <header
          style={{
            display: "flex",
            alignItems: "center",
            gap: 16,
            padding: "0 16px",
            borderBottom: "1px solid var(--colorNeutralStroke2)",
          }}
        >
          <Text weight="bold" size={400}>
            ⬡ MeshWeaver
          </Text>
          <Input contentBefore={<Search20Regular />} placeholder="Search the mesh…" style={{ maxWidth: 360, flex: 1 }} />
          <div style={{ flex: 1 }} />
          {/* Switch back to the classic Blazor shell — same toggle the Vite SPA carries: the
              portal's GET /frontend/blazor sets the mw-frontend override cookie and redirects.
              Also set the cookie client-side so the choice sticks when this app is served
              standalone (no portal endpoint on this origin). */}
          <Tooltip content="Switch back to the classic Blazor portal" relationship="description">
            <Button
              appearance="subtle"
              icon={<ArrowHookUpLeft20Regular />}
              onClick={() => {
                document.cookie = "mw-frontend=Blazor; path=/; max-age=31536000; samesite=lax";
                window.location.href = "/frontend/blazor";
              }}
            >
              Back to classic
            </Button>
          </Tooltip>
          {/* Theme toggle reads localStorage — render only after mount (SSR-consistent). */}
          {mounted ? <ThemeToggle /> : null}
          <Avatar name={userName} size={32} color="colorful" />
        </header>

        <div style={{ display: "grid", gridTemplateColumns: "220px 1fr", minHeight: 0 }}>
          <nav
            style={{
              borderRight: "1px solid var(--colorNeutralStroke2)",
              padding: 8,
              display: "flex",
              flexDirection: "column",
              gap: 2,
              background: "var(--colorNeutralBackground2)",
            }}
          >
            {nav.map((n) => {
              const active = n.path === "" ? currentPath === "" : currentPath === n.path || currentPath.startsWith(`${n.path}/`);
              return (
                <Link
                  key={n.path || "home"}
                  href={hrefForMeshPath(n.path)}
                  style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 8,
                    padding: "8px 10px",
                    borderRadius: 6,
                    textDecoration: "none",
                    color: "inherit",
                    font: "inherit",
                    background: active ? "var(--colorNeutralBackground1Selected)" : "transparent",
                    fontWeight: active ? 600 : 400,
                  }}
                >
                  {n.icon}
                  {n.label}
                </Link>
              );
            })}
          </nav>

          <main style={{ overflow: "auto", padding: 24, minWidth: 0 }}>{children}</main>
        </div>
      </div>
    </FluentProvider>
  );
}
