// NODE EDITING module — the node-bound content editor + the appearance settings surface.
import type { DeploymentModule } from "@meshweaver/react/core";
import { rnLiveControls } from "../rnMeshLive";

const nodeEditing: DeploymentModule = {
  name: "nodeEditing",
  pack: {
    controls: {
      MeshNodeContentEditor: rnLiveControls.MeshNodeContentEditor,
      Appearance: rnLiveControls.Appearance,
    },
  },
};
export default nodeEditing;
