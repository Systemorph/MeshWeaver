import { WorkspaceReference } from "./WorkspaceReference";

export class PathReference extends WorkspaceReference {
    constructor(public path: string) {
        super();
    }
}