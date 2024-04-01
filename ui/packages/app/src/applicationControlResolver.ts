import { viteFolderControlsResolver } from "./viteFolderControlsResolver";
import { ControlModule } from "./ControlModule";

const modules = import.meta.glob<ControlModule>('./controls/*Control.tsx');

export const applicationControlsResolver = viteFolderControlsResolver(modules);