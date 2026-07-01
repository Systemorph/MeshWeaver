import { useState } from "react";
import { Avatar, FluentProvider, Input, Text, webLightTheme } from "@fluentui/react-components";
import { Board20Regular, Search20Regular, Table20Regular, Person20Regular } from "@fluentui/react-icons";
import { RegistryProvider, RenderArea, ScopeProvider, StaticAreaSource, fluentPack } from "@meshweaver/react";
import { portalAreas } from "./sample";

// A MeshWeaver web portal: an app shell (header + nav) around a switchable mesh layout area — the web
// analog of the Blazor portal / MAUI app. The chrome is plain Fluent; the content is the renderer.
const source = new StaticAreaSource(portalAreas);

const nav = [
  { key: "home", label: "Dashboard", icon: <Board20Regular /> },
  { key: "accounts", label: "Accounts", icon: <Table20Regular /> },
  { key: "profile", label: "Profile", icon: <Person20Regular /> },
];

export function Portal() {
  const [area, setArea] = useState("home");
  return (
    <FluentProvider theme={webLightTheme} style={{ height: "100vh" }}>
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
          <Avatar name="Roland Bürgi" size={32} color="colorful" />
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
            {nav.map((n) => (
              <button
                key={n.key}
                onClick={() => setArea(n.key)}
                style={{
                  display: "flex",
                  alignItems: "center",
                  gap: 8,
                  padding: "8px 10px",
                  border: "none",
                  borderRadius: 6,
                  cursor: "pointer",
                  textAlign: "left",
                  font: "inherit",
                  background: area === n.key ? "var(--colorNeutralBackground1Selected)" : "transparent",
                  fontWeight: area === n.key ? 600 : 400,
                }}
              >
                {n.icon}
                {n.label}
              </button>
            ))}
          </nav>

          <main style={{ overflow: "auto", padding: 24, minWidth: 0 }}>
            {/* The renderer core + the Fluent pack (no nested FluentProvider — the shell supplies it). */}
            <RegistryProvider pack={fluentPack}>
              <ScopeProvider source={source} area={area}>
                <RenderArea areaKey={area} />
              </ScopeProvider>
            </RegistryProvider>
          </main>
        </div>
      </div>
    </FluentProvider>
  );
}
