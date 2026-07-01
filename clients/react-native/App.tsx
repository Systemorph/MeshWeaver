import { useEffect, useMemo, useState } from "react";
import { SafeAreaView, ScrollView, StatusBar } from "react-native";
import {
  RegistryProvider,
  ScopeProvider,
  RenderArea,
  StaticAreaSource,
  type AreaSource,
} from "@meshweaver/react/core";
import { Mesh } from "@meshweaver/client-web";
import { rnPack } from "./src/rnPack";
import { sampleArea } from "./src/sample";
import { createLiveSource, type LiveOptions } from "./src/live";
import { ChatComposer } from "./src/chat";
import { ExpoAvRecorder } from "./src/speech/expoRecorder";
import { SpeechTranscriptionClient } from "./src/speech/transcription";
import type { ThreadSubmitter } from "./src/speech/pushToTalk";

// Offline by default (the bundled sample). To render a REAL portal layout area over the wire, fill in
// LIVE — the app connects via @meshweaver/client-web (the gRPC-web Connect+Deliver split) and feeds a
// GrpcAreaSource. RN can't use @grpc/grpc-js (Node http2) or the bidi Open; see README "Live transport".
const LIVE: LiveOptions | null = null;
// const LIVE: LiveOptions = { url: "https://atioz.meshweaver.cloud", token: "mw_…", address: "@app/Home", area: "main" };

// The chat composer + speech pipeline. `namespacePath` anchors new threads (your partition); speech
// posts recorded audio to the CENTRALIZED Whisper endpoint (the portal's /api/speech/transcribe —
// see src/speech/transcription.ts; for a dev container use `speech: { url: "http://localhost:8080",
// path: "/inference" }`). Set CHAT to null to hide the composer entirely.
interface ChatOptions {
  namespacePath: string;
  speech?: { url?: string; token?: string; path?: string; language?: string } | null;
}
const CHAT: ChatOptions | null = null;
// const CHAT: ChatOptions = { namespacePath: "rbuergi", speech: { language: "de" } };

const staticSource = new StaticAreaSource(sampleArea);

export default function App() {
  const [source, setSource] = useState<AreaSource>(staticSource);
  const [submitter, setSubmitter] = useState<ThreadSubmitter | undefined>(undefined);

  useEffect(() => {
    if (!LIVE) return;
    let live: Awaited<ReturnType<typeof createLiveSource>> | null = null;
    createLiveSource(LIVE).then((l) => {
      live = l;
      setSource(l.source);
      // The SAME gRPC-web connection carries thread submissions (Mesh.startThread / Mesh.submitMessage).
      setSubmitter(Mesh.from(l.connection));
    });
    return () => live?.connection.close();
  }, []);

  // Speech seams — created once; the composer hides the mic when speech isn't configured.
  const speech = useMemo(() => {
    if (!CHAT?.speech) return null;
    const url = CHAT.speech.url ?? LIVE?.url;
    if (!url) return null; // no portal to transcribe against
    return {
      recorder: new ExpoAvRecorder(),
      transcriber: new SpeechTranscriptionClient({
        url,
        token: CHAT.speech.token ?? LIVE?.token,
        path: CHAT.speech.path,
        language: CHAT.speech.language,
      }),
      language: CHAT.speech.language,
    };
  }, []);

  return (
    <SafeAreaView style={{ flex: 1, backgroundColor: "#faf9f8" }}>
      <StatusBar />
      <ScrollView contentContainerStyle={{ padding: 16 }} style={{ flex: 1 }}>
        <RegistryProvider pack={rnPack}>
          <ScopeProvider source={source} area="main">
            <RenderArea areaKey="main" />
          </ScopeProvider>
        </RegistryProvider>
      </ScrollView>
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
