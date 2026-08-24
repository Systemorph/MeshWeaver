// ANALYSIS module — the shared-geometry analysis leaves: KpiStrip, Tower, ComparisonBars.
import type { DeploymentModule } from "@meshweaver/react/core";
import { rnAnalysisControls } from "../rnAnalysis";

const analysis: DeploymentModule = { name: "analysis", pack: { controls: { ...rnAnalysisControls } } };
export default analysis;
