"use client";

// The mobile (≤768px) hamburger menu — the React port of the memex portal's MemexMobileMenu
// (memex/Memex.Portal.Shared/Layout/MemexMobileMenu.razor): a full-width sheet under the header
// with a search box (Enter routes to /search), Create New…, and Settings for the current node.

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Divider, Input, MenuItem, MenuList } from "@fluentui/react-components";
import { Add20Regular, Search20Regular, Settings20Regular } from "@fluentui/react-icons";
import { useNavigationState } from "./LiveConnection";

export function MobileMenu({ onClose }: { onClose: () => void }) {
  const router = useRouter();
  const nav = useNavigationState();
  const [query, setQuery] = useState("");

  const go = (href: string) => {
    router.push(href);
    onClose();
  };

  const currentPath = nav.target?.address ?? "";

  return (
    <div
      data-mw-mobile-menu
      style={{
        position: "absolute",
        top: "100%",
        left: 0,
        right: 0,
        zIndex: 300,
        maxHeight: "80vh",
        overflowY: "auto",
        background: "var(--colorNeutralBackground1)",
        borderBottom: "1px solid var(--colorNeutralStroke2)",
        boxShadow: "var(--shadow16)",
      }}
    >
      <div style={{ padding: 8 }}>
        <Input
          contentBefore={<Search20Regular />}
          placeholder="Search..."
          value={query}
          style={{ width: "100%" }}
          onChange={(_, d) => setQuery(d.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && query.trim())
              go(`/search?q=${encodeURIComponent(query.trim())}&hq=${encodeURIComponent("scope:descendants")}`);
          }}
        />
      </div>
      <Divider />
      <MenuList>
        <MenuItem icon={<Add20Regular />} onClick={() => go("/create")}>
          Create New...
        </MenuItem>
        <Divider />
        <MenuItem
          icon={<Settings20Regular />}
          onClick={() => go(currentPath ? `/${currentPath}/Settings` : "/Settings")}
        >
          Settings
        </MenuItem>
      </MenuList>
    </div>
  );
}
