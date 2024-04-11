import { set } from "lodash-es";
import { WorkspaceReferenceBase } from "./WorkspaceReferenceBase";
import { selectByPath } from "../operators/selectByPath";
import { pointerToArray } from "../operators/pointerToArray";

export class WorkspaceReference<T = unknown> extends WorkspaceReferenceBase<T> {
    constructor(public path: string) {
        super();
    }

    get = selectByPath(this.path);

    set(data: object, value: T) {
        set(data, pointerToArray(this.path), value);
    }
}