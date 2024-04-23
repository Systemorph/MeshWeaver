import { Workspace } from "@open-smc/data/src/Workspace";

export interface Renderer {
    readonly dataContextWorkspace: Workspace;
}