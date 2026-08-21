import { useEffect, useMemo, useRef, useState } from "react";
import { SafeAreaView, StatusBar, LogBox, Platform } from "react-native";
import {
  RegistryProvider,
  ScopeProvider,
  StaticAreaSource,
  EmbeddedAreaProvider,
  MeshOpsProvider,
  LocaleProvider,
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
import { attachInstanceStore, currentInstance, discoverInstances, mergeDiscovered, setConnectStatus, type MeshInstance } from "./src/connection";
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

// Threads anchor in the viewer's OWN partition: on the native local mesh that is the device user
// (seeded by the sidecar's DeviceSeed); against a remote portal the configured namespacePath applies.
const chatNamespace = (inst: MeshInstance): string =>
  Platform.OS !== "web" && inst.local ? "device-user" : (CHAT?.namespacePath ?? "");

// 📱 The native-local landing: the device user's own node, rendered with its DECLARED default
// area (the User layout declares the activities — the same landing as the MAUI shell) — the app
// asks for the standard layout instead of naming an area. Web keeps the docs HOME — a same-origin
// viewer is a real portal user whose partition this app cannot know.
const DEVICE_HOME: NavTarget = { address: "device-user", area: "" };
const homeFor = (inst: MeshInstance): NavTarget =>
  Platform.OS !== "web" && inst.local ? DEVICE_HOME : HOME;

// react-native-render-html (the native HTML renderer) still uses React's deprecated `defaultProps`,
// which React 18.3 logs as a dev-only warning per node — suppress that one third-party message so it
// doesn't bury real warnings (harmless; gone in a release build).
LogBox.ignoreLogs([/Support for defaultProps will be removed/]);

/** The device's preferred language (Expo web → navigator.language; native → the same global). */
function deviceLocale(): string | null {
  const nav = (globalThis as { navigator?: { language?: string } }).navigator;
  return nav?.language ?? null;
}

export default function App() {
  ensureWebStyles();
  // No seeding here: the instance list IS the local mesh's MemexInstance nodes (the sidecar seeds
  // its defaults on first boot) — hydrated by attachInstanceStore on the Local connect below.
  return (
    <ThemeProvider>
      {/* The viewer's language. The RN app talks to the sidecar ANONYMOUSLY (no user node to read a
          profile Locale off), so the device language is the authority here — resolved once, to a
          supported tag, exactly as AccessContext.Locale is server-side. */}
      <LocaleProvider locale={deviceLocale()}>
        <AppInner />
      </LocaleProvider>
    </ThemeProvider>
  );
}

function AppInner() {
  const { palette } = useTheme();
  const [nav, setNav] = useState<NavTarget>(() => homeFor(currentInstance()));
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

  // 📱 On a phone the app IS the onboarding until a mesh acks: the bundled sample
  // ("MeshWeaver on React Native", Ada Lovelace, a stubbed Save) is a web/e2e artifact and
  // must never greet a person — it reads as a broken login form. Web keeps the sample: the
  // Playwright e2e drives it from a static export with no mesh to ack.
  const wasLive = useRef(false);
  useEffect(() => {
    if (Platform.OS === "web") return;
    if (!liveConnected && clientScreen == null) setClientScreen("instances");
    // Close the gate screens exactly ONCE, on the not-connected → connected transition —
    // a connected user opening the switcher on purpose must not have it snap shut. The
    // profile onboarding closes the same way: completing it reconnects, and the ack lands here.
    if (liveConnected && !wasLive.current)
      setClientScreen((c) => (c === "instances" || c === "onboarding" ? null : c));
    wasLive.current = liveConnected;
  }, [liveConnected, clientScreen]);
  const reconnect = () => {
    // On a phone the onboarding stays visible until the mesh ACKS (the transition effect
    // closes it) — closing eagerly here made a tap read as "connect just closes".
    if (Platform.OS === "web") setClientScreen(null);
    setNav(homeFor(currentInstance()));
    setLiveConnected(false);
    wasLive.current = false;
    setInstanceTick((t) => t + 1);
  };

  useEffect(() => {
    const inst = currentInstance();
    if (!inst.url) return;
    let live: Awaited<ReturnType<typeof createLiveSource>> | null = null;
    let cancelled = false;
    setConnectStatus(`Connecting to ${inst.name}…`);
    createLiveSource({ url: inst.url, token: inst.token, address: nav.address, area: nav.area })
      .then((l) => {
        if (cancelled) {
          l.connection.close();
          return;
        }
        live = l;
        setSource(l.source);
        setLiveConnected(true);
        setConnectStatus("");
        // The SAME gRPC-web connection carries thread submissions (Mesh.startThread / Mesh.submitMessage)
        // AND the nested streams that `@@("area/X")` layout-area embeds open.
        setSubmitter(Mesh.from(l.connection));
        setEmbedFactory(() => createGrpcEmbeddedFactory(l.connection));
        // The full MeshOps over the same connection — renderMarkdown (server Markdig) + the per-view kernel
        // anchor the interactive markdown + runnable code cells; the kernel activity lives in CHAT's partition.
        setMeshOps(buildMeshOps(l.connection, inst.url, chatNamespace(inst), inst.token));
        // The LOCAL mesh is the instance STORE: hydrate the switcher's mesh list from its
        // MemexInstance nodes. A remote connect detaches (the in-memory cache keeps serving).
        void attachInstanceStore(
          inst.local ? Mesh.from(l.connection, undefined, { url: inst.url, token: inst.token }) : null,
        );
        // The local mesh is ALSO where the deployments you operate are recorded
        // (nodeType:Hosting/Deployment) — fold them into the switcher, like remote fleets.
        if (inst.local)
          void discoverInstances(inst).then((d) => { if (d.length) mergeDiscovered(d); }).catch(() => {});
        // 📱 First launch on the device mesh: no device user yet → the app opens INTO the
        // onboarding dialog (the RN twin of MAUI's OnboardingPage). "Get started" posts the
        // profile (POST /api/mesh/onboard) and the following reconnect ack closes the screen.
        if (Platform.OS !== "web" && inst.local)
          void fetch(`${inst.url.replace(/\/+$/, "")}/api/mesh/onboard`)
            .then((r) => (r.ok ? r.json() : null))
            .then((j: { onboarded?: boolean } | null) => {
              if (j && j.onboarded === false) setClientScreen("onboarding");
            })
            .catch(() => {});
      })
      .catch((e) => {
        // Release builds swallow console — surface the failure where the user IS (the
        // onboarding renders this status), instead of a screen that silently reopens.
        setConnectStatus(`${inst.name}: connect failed — ${e?.message ?? String(e)}`);
      });
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
                  home={homeFor(currentInstance())}
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
          namespacePath={chatNamespace(currentInstance())}
          recorder={speech?.recorder}
          transcriber={speech?.transcriber}
          language={speech?.language}
        />
      )}
    </SafeAreaView>
  );
}
