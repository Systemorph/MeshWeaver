import { get, trimStart } from "lodash-es";
import { WorkspaceReferenceBase } from "./WorkspaceReferenceBase";

export class WorkspaceReference<T = unknown> extends WorkspaceReferenceBase<T> {
    constructor(public path: string) {
        super();
    }

    get = (data: unknown): T => get(data, pointerToPath(this.path));
}

const pointerToPath = (path: string) => trimStart(path, "/").split("/");