// MESH BROWSE module — the search/catalog surfaces (the tabbed home's engine): MeshSearch (scope
// tabs, Icons grid, union queries) and MeshNodeCollection.
import type { DeploymentModule } from "@meshweaver/react/core";
import { rnLiveControls } from "../rnMeshLive";

const meshBrowse: DeploymentModule = {
  name: "meshBrowse",
  pack: {
    controls: {
      MeshSearch: rnLiveControls.MeshSearch,
      MeshNodeCollection: rnLiveControls.MeshNodeCollection,
    },
  },
};
export default meshBrowse;
