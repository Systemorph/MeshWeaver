// EXAMPLE deployment module — the shape any client module ships (an npm package or a repo path):
// one default-exported DeploymentModule. `pack` contributes leaves for control $types the base
// pack lacks (a bespoke visual only a client needs — the reason a module reaches for a CLIENT
// extension at all; server-declared standard controls never need one). Referenced by
// deployment/examples/acme.json; the generator turns that reference into a static import.
import { Text, View } from "react-native";
import type { DeploymentModule, UiControl } from "@meshweaver/react/core";

const AcmeBadge = ({ control }: { control: UiControl }) => (
  <View style={{ backgroundColor: "#7c3aed", borderRadius: 6, paddingHorizontal: 10, paddingVertical: 4, alignSelf: "flex-start" }}>
    <Text style={{ color: "#ffffff", fontWeight: "600" }}>{String(control.data ?? "ACME")}</Text>
  </View>
);

const acmeModule: DeploymentModule = {
  name: "acme-badge",
  pack: {
    controls: { AcmeBadge },
  },
};

export default acmeModule;
