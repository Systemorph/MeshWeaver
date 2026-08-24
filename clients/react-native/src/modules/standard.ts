// The STANDARD module set — what the stock Memex deployment ships (deployment/default.json lists
// the same seven; deployment.test.tsx guards the two against drifting apart). Tests compose this
// over the core pack to render "the full app"; a lean deployment simply lists fewer.
import type { DeploymentModule } from "@meshweaver/react/core";
import threads from "./threads";
import meshBrowse from "./meshBrowse";
import nodeEditing from "./nodeEditing";
import data from "./data";
import documents from "./documents";
import analysis from "./analysis";
import media from "./media";

export const standardModules: readonly DeploymentModule[] = [
  threads,
  meshBrowse,
  nodeEditing,
  data,
  documents,
  analysis,
  media,
];

import { composeDeployment } from "@meshweaver/react/core";
import { rnPack } from "../rnPack";

/** The FULL stock app pack (core + the standard set) — what tests render, matching default.json. */
export const fullPack = composeDeployment(rnPack, standardModules);
