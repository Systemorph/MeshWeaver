// THREADS module — the chat surface: the live ThreadChat leaf (composer with @-mentions + speech)
// and the message bubble. A deployment without threads ships no chat UI (and, with media also
// absent, no expo-av at all — the composer's recorder rides in via ThreadChat).
import { Text, View } from "react-native";
import { str as s, useResolve, type ControlComponent, type DeploymentModule } from "@meshweaver/react/core";
import { rnLiveControls } from "../rnMeshLive";

const ThreadMessageBubble: ControlComponent = ({ control }) => {
  const role = s(useResolve(control.role)) || "user";
  const mine = /user/i.test(role);
  const text = s(useResolve(control.message)) || s(useResolve(control.data));
  return (
    <View style={{ flexDirection: "row", justifyContent: mine ? "flex-end" : "flex-start" }}>
      <View
        style={{
          maxWidth: "86%",
          borderRadius: 10,
          paddingHorizontal: 12,
          paddingVertical: 8,
          backgroundColor: mine ? "#e1ebf7" : "#f0f0f0",
        }}
      >
        <Text style={{ fontSize: 14, color: "#242424" }}>{text}</Text>
      </View>
    </View>
  );
};

const threads: DeploymentModule = {
  name: "threads",
  pack: { controls: { ThreadChat: rnLiveControls.ThreadChat, ThreadMessageBubble } },
};
export default threads;
