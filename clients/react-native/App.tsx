import { SafeAreaView, ScrollView, StatusBar } from "react-native";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource } from "@meshweaver/react/core";
import { rnPack } from "./src/rnPack";
import { sampleArea } from "./src/sample";

// For LIVE mesh data, swap StaticAreaSource for GrpcAreaSource (from @meshweaver/react/core) wired to a
// grpc-web transport — RN can't use @grpc/grpc-js (Node http2); see README "Live transport".
const source = new StaticAreaSource(sampleArea);

export default function App() {
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
