import { useEffect, useMemo, useState } from "react";
import { SafeAreaView, StatusBar } from "react-native";
import { RegistryProvider, ScopeProvider, StaticAreaSource, type AreaSource } from "@meshweaver/react/core";
import { Mesh } from "@meshweaver/client-web";
import { rnPack } from "./src/rnPack";
import { sampleArea } from "./src/sample";
import { createLiveSource } from "./src/live";
import { NavContext, CurrentAddressContext, type NavTarget } from "./src/nav";
import { Shell, HOME } from "./src/Shell";
import { ensureWebStyles } from "./src/webStyles";
import { currentInstance } from "./src/connection";
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
// Speech enabled for the local-simulator demo: a standalone build has no served origin, so point
// speech at the local Whisper container explicitly (deploy/whisper → http://localhost:8080/inference;
// localhost reaches the host Mac from the simulator). Drop `speech.url`/`speech.path` in a mesh-served
// deployment and the endpoint follows the current instance (see the useMemo below).
const CHAT: ChatOptions | null = {
  namespacePath: "rbuergi",
  speech: { url: "http://localhost:8080", path: "/inference", language: "de" },
};
// const CHAT: ChatOptions | null = null;

export default function App() {
  ensureWebStyles();
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
        // The SAME gRPC-web connection carries thread submissions (Mesh.startThread / Mesh.submitMessage).
        setSubmitter(Mesh.from(l.connection));
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
