import { useEffect, useState } from "react";
import { SafeAreaView, ScrollView, StatusBar } from "react-native";
import {
  RegistryProvider,
  ScopeProvider,
  RenderArea,
  StaticAreaSource,
  type AreaSource,
} from "@meshweaver/react/core";
import { rnPack } from "./src/rnPack";
import { sampleArea } from "./src/sample";
import { createLiveSource, type LiveOptions } from "./src/live";

// Offline by default (the bundled sample). To render a REAL portal layout area over the wire, fill in
// LIVE — the app connects via @meshweaver/client-web (the gRPC-web Connect+Deliver split) and feeds a
// GrpcAreaSource. RN can't use @grpc/grpc-js (Node http2) or the bidi Open; see README "Live transport".
const LIVE: LiveOptions | null = null;
// const LIVE: LiveOptions = { url: "https://atioz.meshweaver.cloud", token: "mw_…", address: "@app/Home", area: "main" };

const staticSource = new StaticAreaSource(sampleArea);

export default function App() {
  const [source, setSource] = useState<AreaSource>(staticSource);

  useEffect(() => {
    if (!LIVE) return;
    let live: Awaited<ReturnType<typeof createLiveSource>> | null = null;
    createLiveSource(LIVE).then((l) => {
      live = l;
      setSource(l.source);
    });
    return () => live?.connection.close();
  }, []);

  return (
    <SafeAreaView style={{ flex: 1, backgroundColor: "#faf9f8" }}>
      <StatusBar />
      <ScrollView contentContainerStyle={{ padding: 16 }}>
        <RegistryProvider pack={rnPack}>
          <ScopeProvider source={source} area="main">
            <RenderArea areaKey="main" />
          </ScopeProvider>
        </RegistryProvider>
      </ScrollView>
    </SafeAreaView>
  );
}
