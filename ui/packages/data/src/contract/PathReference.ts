import { WorkspaceReference } from "./WorkspaceReference";

export class PathReference<T = unknown> extends WorkspaceReference {
    constructor(public path: string) {
        super();
    }
}