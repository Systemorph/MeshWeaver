"use client";

// The portal chrome around the routed mesh area — the React port of the Blazor shell's
// PortalLayoutBase + the memex MainLayout (which removes the left nav column: full-width,
// GitHub-style content). The chrome lives in the App Router layout, so it renders in the FIRST
// HTML flush and persists across navigations while pages stream in below it.
//
// Desktop (>768px, the ViewportInformation breakpoint) header, left to right — the same order as
// PortalLayoutBase.razor:
//   MESHWEAVER logo · search bar (ALL remaining space) · Node/Mesh/AI menus · Settings ·
//   notification bell · side-panel toggle · theme toggle · avatar menu.
// Mobile (≤768px): logo · avatar · hamburger, with the MemexMobileMenu sheet; the menu closes
// automatically when the viewport grows past the breakpoint (PortalLayoutBase.OnParametersSet).
//
// The main row hosts the routed page next to the right-docked chat/content side panel
// (SidePanelPane), exactly like the Blazor splitter.

import { useEffect, useState, type ReactNode } from "react";
import Link from "next/link";
import { Button, FluentProvider, Text } from "@fluentui/react-components";
import { Dismiss24Regular, Navigation24Regular } from "@fluentui/react-icons";
import { ThemeToggle } from "@meshweaver/react";
import { Breadcrumbs } from "./Breadcrumbs";
import { HeaderMenus } from "./HeaderMenus";
import { MeshNavigationProvider } from "./MeshNavigation";
import { MeshWeaverLogo } from "./MeshWeaverLogo";
import { MobileMenu } from "./MobileMenu";
import { NotificationCenter } from "./NotificationCenter";
import { SearchBar } from "./SearchBar";
import { SidePanelPane, SidePanelProvider, SidePanelToggle } from "./SidePanel";
import { UserProfileMenu } from "./UserProfileMenu";
import { useHydratedTheme } from "./useHydratedTheme";
import { useIsMobile } from "./useViewport";

export function PortalShell({ children }: { children: ReactNode }) {
  const { theme, mounted } = useHydratedTheme();
  const isMobile = useIsMobile();
  const [navMenuOpen, setNavMenuOpen] = useState(false);

  // Close the mobile menu when the viewport switches to desktop (PortalLayoutBase rule).
  useEffect(() => {
    if (!isMobile && navMenuOpen) setNavMenuOpen(false);
  }, [isMobile, navMenuOpen]);

  return (
    <FluentProvider theme={theme} style={{ height: "100vh" }}>
      <MeshNavigationProvider>
        <SidePanelProvider>
          <div
            style={{
              display: "grid",
              gridTemplateRows: "52px auto 1fr",
              height: "100vh",
            }}
          >
            <header
              data-mw-header
              style={{
                position: "relative",
                display: "flex",
                alignItems: "center",
                gap: 8,
                padding: "0 12px",
                borderBottom: "1px solid var(--colorNeutralStroke2)",
                minWidth: 0,
              }}
            >
              <Link
                href="/"
                data-mw-logo
                aria-label="MeshWeaver"
                style={{
                  textDecoration: "none",
                  color: "inherit",
                  flexShrink: 0,
                  display: "inline-flex",
                  alignItems: "center",
                  gap: 8,
                }}
              >
                <MeshWeaverLogo size={28} />
                <Text weight="bold" size={400} style={{ whiteSpace: "nowrap" }}>
                  MESHWEAVER
                </Text>
              </Link>

              {isMobile ? (
                <>
                  <div style={{ flex: 1 }} />
                  <UserProfileMenu />
                  <Button
                    appearance="transparent"
                    className="navigation-button"
                    title="Menu"
                    aria-label="Menu"
                    icon={
                      navMenuOpen ? (
                        <Dismiss24Regular />
                      ) : (
                        <Navigation24Regular />
                      )
                    }
                    onClick={() => setNavMenuOpen((open) => !open)}
                  />
                  {navMenuOpen && (
                    <MobileMenu onClose={() => setNavMenuOpen(false)} />
                  )}
                </>
              ) : (
                <>
                  {/* The search bar takes ALL remaining header space. */}
                  <SearchBar />
                  <HeaderMenus />
                  <NotificationCenter />
                  <SidePanelToggle />
                  {/* Theme toggle reads localStorage — render only after mount (SSR-consistent). */}
                  {mounted ? <ThemeToggle /> : null}
                  <UserProfileMenu />
                </>
              )}
            </header>

            {/* Breadcrumb row — the Blazor shell's ⌂ Home › … bar (hidden on mobile). */}
            {!isMobile ? <Breadcrumbs /> : <div />}

            <div style={{ display: "flex", minHeight: 0 }}>
              <main
                style={{ flex: 1, overflow: "auto", padding: 24, minWidth: 0 }}
              >
                {children}
              </main>
              {!isMobile && <SidePanelPane />}
            </div>
          </div>
        </SidePanelProvider>
      </MeshNavigationProvider>
    </FluentProvider>
  );
}
