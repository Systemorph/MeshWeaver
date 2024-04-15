import { set } from "lodash-es";
import { selectByPath } from "../operators/selectByPath";
import { pointerToArray } from "../operators/pointerToArray";
import { WorkspaceReference } from "./WorkspaceReference";

export abstract class PathReferenceBase<T = unknown> extends WorkspaceReference<T> {
    protected abstract get path(): string;

    get(data: any) {
        return selectByPath(this.path)(data) as T;
    }

    set(data: object, value: T) {
        set(data, pointerToArray(this.path), value);
    }
}

export class PathReference<T = unknown> extends PathReferenceBase {
    constructor(public pointer: string) {
        super();
    }

    protected get path() {
        return this.pointer;
    }
}