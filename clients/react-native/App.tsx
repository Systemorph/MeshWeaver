import { useEffect, useMemo, useState } from "react";
import { SafeAreaView, StatusBar, LogBox } from "react-native";
import {
  RegistryProvider,
  ScopeProvider,
  StaticAreaSource,
  EmbeddedAreaProvider,
  MeshOpsProvider,
  createGrpcEmbeddedFactory,
  type AreaSource,
  type AreaSourceFactory,
  type MeshOps,
} from "@meshweaver/react/core";
import { Mesh } from "@meshweaver/client-web";
import { rnPack } from "./src/rnPack";
import { sampleArea } from "./src/sample";
import { createLiveSource } from "./src/live";
import { buildMeshOps } from "./src/liveOps";
import { NavContext, CurrentAddressContext, type NavTarget } from "./src/nav";
import { Shell, HOME } from "./src/Shell";
import { ensureWebStyles } from "./src/webStyles";
import { currentInstance, seedDefaultInstances } from "./src/connection";
import { type ClientDestination } from "./src/screens";
import { ThemeProvider, useTheme } from "./src/theme";
import { ChatComposer } from "./src/chat";
import { ExpoAvRecorder } from "./src/speech/expoRecorder";
import { SpeechTranscriptionClient } from "./src/speech/transcription";
import type { ThreadSubmitter } from "./src/speech/pushToTalk";

// The client connects to the CURRENT mesh instance — "Local" is the mesh that served this app
// (same origin, anonymous, no CORS); a remote instance is a portal the user added by URL + token
// (see screens.tsx → ConnectScreen). The shell drives navigation; each target re-subscribes the
// live source, and switching instance (instanceTick) reconnects and returns Home.
//
// Until a mesh ACKS the connect, the app is the bundled OFFLINE demo: the sample tree renders
// under its "main" area (the metro-stub / README contract, and what the Playwright e2e drives
// against a static file server). connect() only resolves on a real ack, so a non-mesh origin
// can never swap the sample out for an empty live source.

// The chat composer + CENTRALIZED speech pipeline (distinct from the shell's VoiceScreen, which is
// the browser's on-device Web Speech API). `namespacePath` anchors new threads (your partition);
// speech records via expo-av and posts the audio to the portal's `POST /api/speech/transcribe`
// (the centralized Whisper container — see src/speech/transcription.ts; for a dev container use
// `speech: { url: "http://localhost:8080", path: "/inference" }`). Set CHAT to null to hide the
// composer entirely. Submission rides the SAME gRPC-web connection the renderer uses.
interface ChatOptions {
  namespacePath: string;
  speech?: { url?: string; token?: string; path?: string; language?: string } | null;
}
// Speech follows the CURRENT mesh instance: with no `speech.url`/`speech.path`, the transcription client
// POSTs to `{instance}/api/speech/transcribe` — the endpoint every backend now bakes in (the portal AND
// the local sidecar Memex.LocalMesh), so voice input works in every shell (web, the macOS/Windows desktop
// apps, and against a remote portal) with no separate container URL. To point at a bare dev Whisper
// container instead, set `speech: { url: "http://localhost:8080", path: "/inference" }`.
const CHAT: ChatOptions | null = {
  namespacePath: "rbuergi",
  speech: { language: "de" },
};
// const CHAT: ChatOptions | null = null;

// react-native-render-html (the native HTML renderer) still uses React's deprecated `defaultProps`,
// which React 18.3 logs as a dev-only warning per node — suppress that one third-party message so it
// doesn't bury real warnings (harmless; gone in a release build).
LogBox.ignoreLogs([/Support for defaultProps will be removed/]);

export default function App() {
  ensureWebStyles();
  seedDefaultInstances(); // populate the switcher with the known environments on first run (idempotent)
  return (
    <ThemeProvider>
      <AppInner />
    </ThemeProvider>
  );
}

function AppInner() {
  const { palette } = useTheme();
  const [nav, setNav] = useState<NavTarget>(HOME);
  const [clientScreen, setClientScreen] = useState<ClientDestination | null>(null);
  const [instanceTick, setInstanceTick] = useState(0);
  const [source, setSource] = useState<AreaSource>(() => new StaticAreaSource(sampleArea));
  const [liveConnected, setLiveConnected] = useState(false);
  const [submitter, setSubmitter] = useState<ThreadSubmitter | undefined>(undefined);
  // The factory `@@` layout-area embeds (LayoutAreaControl) open their nested area streams through.
  const [embedFactory, setEmbedFactory] = useState<AreaSourceFactory | null>(null);
  // The MeshOps surface (interactive-markdown render + kernel, thread submit, node ops) the tree consumes
  // via useMeshOps — built at the app level over the live connection, exactly like portal-next's adaptOps.
  const [meshOps, setMeshOps] = useState<MeshOps | null>(null);

  const navigate = (t: NavTarget) => {
    setClientScreen(null);
    setNav(t);
  };
  const reconnect = () => {
    setClientScreen(null);
    setNav(HOME);
    setInstanceTick((t) => t + 1);
  };

  useEffect(() => {
    const inst = currentInstance();
    if (!inst.url) return;
    let live: Awaited<ReturnType<typeof createLiveSource>> | null = null;
    let cancelled = false;
    createLiveSource({ url: inst.url, token: inst.token, address: nav.address, area: nav.area })
      .then((l) => {
        if (cancelled) {
          l.connection.close();
          return;
        }
        live = l;
        setSource(l.source);
        setLiveConnected(true);
        // The SAME gRPC-web connection carries thread submissions (Mesh.startThread / Mesh.submitMessage)
        // AND the nested streams that `@@("area/X")` layout-area embeds open.
        setSubmitter(Mesh.from(l.connection));
        setEmbedFactory(() => createGrpcEmbeddedFactory(l.connection));
        // The full MeshOps over the same connection — renderMarkdown (server Markdig) + the per-view kernel
        // anchor the interactive markdown + runnable code cells; the kernel activity lives in CHAT's partition.
        setMeshOps(buildMeshOps(l.connection, inst.url, CHAT?.namespacePath ?? ""));
      })
      .catch((e) => { console.warn("[live] connect failed:", e?.message ?? String(e)); /* shell stays on the last-good source */ });
    return () => {
      cancelled = true;
      live?.connection.close();
    };
  }, [nav.address, nav.area, instanceTick]);

  // Speech seams — the transcription endpoint follows the CURRENT instance (or an explicit
  // CHAT.speech.url override); the composer hides the mic when speech isn't configured.
  const speech = useMemo(() => {
    if (!CHAT?.speech) return null;
    const inst = currentInstance();
    const url = CHAT.speech.url ?? inst.url;
    if (!url) return null; // no portal to transcribe against
    return {
      recorder: new ExpoAvRecorder(),
      transcriber: new SpeechTranscriptionClient({
        url,
        token: CHAT.speech.token ?? inst.token,
        path: CHAT.speech.path,
        language: CHAT.speech.language,
      }),
      language: CHAT.speech.language,
    };
  }, [instanceTick]);

  // Offline (no ack yet): the sample tree's root area is "main"; live nav areas only exist
  // once a mesh is streaming.
  const effNav = liveConnected ? nav : { ...nav, area: "main" };

  return (
    <SafeAreaView style={{ flex: 1, backgroundColor: palette.appBg }}>
      <StatusBar />
      <RegistryProvider pack={rnPack}>
       <MeshOpsProvider ops={meshOps}>
        <EmbeddedAreaProvider factory={embedFactory}>
          <NavContext.Provider value={navigate}>
            <CurrentAddressContext.Provider value={nav.address}>
              <ScopeProvider source={source} area={effNav.area}>
                <Shell
                  source={source}
                  nav={effNav}
                  clientScreen={clientScreen}
                  onNavigate={navigate}
                  onClientScreen={setClientScreen}
                  onReconnect={reconnect}
                />
              </ScopeProvider>
            </CurrentAddressContext.Provider>
          </NavContext.Provider>
        </EmbeddedAreaProvider>
       </MeshOpsProvider>
      </RegistryProvider>
      {CHAT && (
        <ChatComposer
          submitter={submitter}
          namespacePath={CHAT.namespacePath}
          recorder={speech?.recorder}
          transcriber={speech?.transcriber}
          language={speech?.language}
        />
      )}
    </SafeAreaView>
  );
}
