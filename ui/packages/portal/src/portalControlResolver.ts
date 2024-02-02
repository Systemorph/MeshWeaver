import { folderControlsResolver } from "@open-smc/application/src/folderControlsResolver";

const context = require.context('./controls', false, /Control\.tsx$/, "lazy");

export const portalControlsResolver = folderControlsResolver(context);