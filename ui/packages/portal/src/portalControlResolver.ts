import { webpackFolderControlsResolver } from "packages/application/src/webpackFolderControlsResolver";

const context = require.context('./controls', false, /Control\.tsx$/, "lazy");

export const portalControlsResolver = webpackFolderControlsResolver(context);