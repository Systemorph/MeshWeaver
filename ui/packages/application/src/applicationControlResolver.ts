import { folderControlsResolver } from "./folderControlsResolver";

const context = require.context('./controls', false, /Control\.tsx$/, "lazy");

export const applicationControlsResolver = folderControlsResolver(context);