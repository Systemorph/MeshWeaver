// DATA module — the analytical grids/charts beyond the core DataGrid: PivotGrid + Chart.
import type { DeploymentModule } from "@meshweaver/react/core";
import { rnDataControls } from "../rnData";

const data: DeploymentModule = { name: "data", pack: { controls: { ...rnDataControls } } };
export default data;
