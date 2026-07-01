import { useEffect, useState } from "react";
import { SafeAreaView, ScrollView, StatusBar } from "react-native";
import {
  RegistryProvider,
  ScopeProvider,
  RenderArea,
  StaticAreaSource,
  type AreaSource,
  type AreaTree,
} from "@meshweaver/react/core";
import { rnPack } from "./src/rnPack";
import { sampleArea } from "./src/sample";
import { createLiveSource, type LiveOptions } from "./src/live";

// When this app is served BY the mesh host (Memex.LocalMesh serves the web build on the same origin as its
// gRPC endpoint), connect live to that same origin — no CORS, no config. Anonymous read (empty token) is
// enough for the public Doc partition. Off the web (native, or opened without a mesh), fall back to the
// bundled offline sample.
const sameOrigin = typeof window !== "undefined" && window.location ? window.location.origin : "";
const LIVE: LiveOptions | null = sameOrigin
  ? { url: sameOrigin, token: "", address: "Doc/Architecture", area: "Overview" }
  : null;

const rootArea = LIVE ? LIVE.area : "main";
const emptyTree: AreaTree = { areas: {}, data: {} };
const initialSource = new StaticAreaSource(LIVE ? emptyTree : sampleArea);

export default function App() {
  const [source, setSource] = useState<AreaSource>(initialSource);

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
          <ScopeProvider source={source} area={rootArea}>
            <RenderArea areaKey={rootArea} />
          </ScopeProvider>
        </RegistryProvider>
      </ScrollView>
    </SafeAreaView>
  );
}
