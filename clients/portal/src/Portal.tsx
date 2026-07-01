import { useEffect, useMemo, useState } from "react";
import {
  Avatar,
  Button,
  FluentProvider,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Tooltip,
} from "@fluentui/react-components";
import {
  ArrowHookUpLeft20Regular,
  Board20Regular,
  Book20Regular,
  Search20Regular,
  Table20Regular,
  Person20Regular,
} from "@fluentui/react-icons";
import {
  MeshAreaView,
  EmbeddedAreaProvider,
  GrpcAreaSource,
  RegistryProvider,
  RenderArea,
  ScopeProvider,
  StaticAreaSource,
  ThemeToggle,
  fluentPack,
  useThemeMode,
} from "@meshweaver/react";
import type { AreaSource } from "@meshweaver/react";
import { portalAreas } from "./sample";
import { DEFAULT_ROOT_AREA, connectLive, createLiveAreaSource, type LiveMesh } from "./live";

// A MeshWeaver web portal: an app shell (header + nav) around a mesh layout area — the web analog
// of the Blazor portal / MAUI app. The chrome is plain Fluent; the content is the renderer.
//
// LIVE MODE: on load the shell joins the mesh over same-origin gRPC-web (see live.ts for the
// route + token bootstrap) and renders REAL layout areas: the hash route `#/{meshPath}` addresses
// any mesh node's default area (`/app/#/Doc/GUI` mirrors the Blazor portal's /Doc/GUI), and no
// hash renders the signed-in user's home — what Blazor shows at /{user}. ThreadChat works through
// MeshOpsProvider (MeshAreaView's ops prop, wired to Mesh.from(connection)).
// When the connection cannot be established (no portal on this origin, or the session is not
// authenticated) the shell falls back to the bundled SAMPLE data with an "offline sample mode"
// banner — the same demo the standalone `vite dev` shows.
const sampleSource = new StaticAreaSource(portalAreas);

const sampleNav = [
  { key: "home", label: "Dashboard", icon: <Board20Regular /> },
  { key: "accounts", label: "Accounts", icon: <Table20Regular /> },
  { key: "profile", label: "Profile", icon: <Person20Regular /> },
];

type LiveState =
  | { kind: "connecting" }
  | { kind: "live"; mesh: LiveMesh }
  | { kind: "offline"; reason: string };

/** The mesh path addressed by the hash route (`#/Doc/GUI` → `Doc/GUI`); "" when no route is set. */
function readHashPath(): string {
  const h = window.location.hash;
  if (!h.startsWith("#/")) return "";
  return decodeURIComponent(h.slice(2)).replace(/\/+$/, "");
}

function useHashPath(): string {
  const [path, setPath] = useState(readHashPath);
  useEffect(() => {
    const onChange = () => setPath(readHashPath());
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  return path;
}

export function Portal() {
  const [sampleArea, setSampleArea] = useState("home");
  const [live, setLive] = useState<LiveState>({ kind: "connecting" });
  const [areaErrors, setAreaErrors] = useState<Record<string, string>>({});
  const hashPath = useHashPath();
  // Light/dark/system — persisted under localStorage["theme"] (the Blazor portal's key), following
  // the OS preference in system mode. All chrome below uses design tokens, so it restyles with it.
  const { theme } = useThemeMode();

  // Join the mesh once on mount; fall back to the sample when the origin has no portal / session.
  useEffect(() => {
    let cancelled = false;
    let mesh: LiveMesh | null = null;
    connectLive().then(
      (m) => {
        mesh = m;
        if (cancelled) m.close();
        else setLive({ kind: "live", mesh: m });
      },
      (error) => {
        if (!cancelled) setLive({ kind: "offline", reason: String((error as Error)?.message ?? error) });
      },
    );
    return () => {
      cancelled = true;
      mesh?.close();
    };
  }, []);

  const connection = live.kind === "live" ? live.mesh.connection : null;
  // One live source per visited path, kept for the connection's lifetime (instant back-nav; the
  // area stream stays subscribed). The map dies with the connection.
  const liveSources = useMemo(() => new Map<string, AreaSource>(), [connection]);
  const livePath = live.kind === "live" ? hashPath || live.mesh.userId : "";
  let liveSource: AreaSource | null = null;
  if (connection && livePath) {
    liveSource = liveSources.get(livePath) ?? null;
    if (!liveSource) {
      liveSource = createLiveAreaSource(connection, livePath, (error) =>
        setAreaErrors((e) => ({ ...e, [livePath]: String((error as Error)?.message ?? error) })),
      );
      liveSources.set(livePath, liveSource);
    }
  }

  const liveNav = [
    { key: "", label: "Home", icon: <Board20Regular /> },
    { key: "Doc", label: "Documentation", icon: <Book20Regular /> },
  ];
  const isLive = live.kind === "live";
  const userName = isLive ? live.mesh.userId : "Guest";

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
          {/* The mirror of the Blazor user menu's "Try the new frontend": switch back to the classic
              shell. Navigates to the portal's frontend-toggle endpoint (GET /frontend/blazor), which
              sets the mw-frontend override cookie to Blazor and redirects to the classic portal —
              fully reversible from the Blazor side. Also clears the cookie client-side so the choice
              sticks even when the React app is served standalone (no portal endpoint on this origin). */}
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
          <ThemeToggle />
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
            {(isLive ? liveNav : sampleNav).map((n) => {
              const active = isLive ? hashPath === n.key || (!hashPath && n.key === "") : sampleArea === n.key;
              return (
                <button
                  key={n.key}
                  onClick={() => {
                    if (isLive) window.location.hash = n.key ? `#/${n.key}` : "#/";
                    else setSampleArea(n.key);
                  }}
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
                    background: active ? "var(--colorNeutralBackground1Selected)" : "transparent",
                    fontWeight: active ? 600 : 400,
                  }}
                >
                  {n.icon}
                  {n.label}
                </button>
              );
            })}
          </nav>

          <main style={{ overflow: "auto", padding: 24, minWidth: 0 }}>
            {live.kind === "connecting" && (
              <div data-mw-connecting style={{ display: "flex", justifyContent: "center", padding: 48 }}>
                <Spinner label="Connecting to the mesh…" />
              </div>
            )}

            {live.kind === "offline" && (
              <>
                <div data-mw-offline style={{ marginBottom: 16 }}>
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      Offline sample mode — no live mesh connection on this origin ({live.reason}). Showing bundled
                      sample data.
                    </MessageBarBody>
                  </MessageBar>
                </div>
                {/* The renderer core + the Fluent pack (no nested FluentProvider — the shell supplies it). */}
                <RegistryProvider pack={fluentPack}>
                  <ScopeProvider source={sampleSource} area={sampleArea}>
                    <RenderArea areaKey={sampleArea} />
                  </ScopeProvider>
                </RegistryProvider>
              </>
            )}

            {isLive && (
              <div data-mw-live-area data-mw-path={livePath} style={{ height: "100%" }}>
                {areaErrors[livePath] ? (
                  <div data-mw-area-error>
                    <MessageBar intent="error">
                      <MessageBarBody>
                        Live area “{livePath}” failed: {areaErrors[livePath]}
                      </MessageBarBody>
                    </MessageBar>
                  </div>
                ) : liveSource && connection ? (
                  // The live renderer: the node's default layout area streamed over gRPC-web,
                  // with the thread ops surface (ThreadChat) provided via MeshOpsProvider and
                  // nested LayoutAreaControl embeds hydrating through their own live sources.
                  <EmbeddedAreaProvider
                    factory={(address, ref) => {
                      const src = new GrpcAreaSource(connection, address, {
                        area: ref.area ?? "",
                        id: ref.id as string | undefined,
                        layout: ref.layout,
                      });
                      void src.start();
                      return { source: src, rootArea: ref.area ?? "" };
                    }}
                  >
                    <MeshAreaView source={liveSource} rootArea={DEFAULT_ROOT_AREA} theme={theme} ops={live.mesh.ops} />
                  </EmbeddedAreaProvider>
                ) : null}
              </div>
            )}
          </main>
        </div>
      </div>
    </FluentProvider>
  );
}
