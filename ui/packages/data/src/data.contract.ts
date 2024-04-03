import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Data.SubscribeRequest")
export class SubscribeRequest {
    constructor(public id: string, public workspaceReference: unknown) {
    }
}

@type("OpenSmc.Data.UnsubscribeDataRequest")
export class UnsubscribeDataRequest {
    constructor(public ids: string[]) {
    }
}

@type("OpenSmc.Data.DataChangedEvent")
export class DataChangedEvent {
    constructor(public id: string, public change: object) {
    }
}

export abstract class WorkspaceReference {
    abstract toJsonPath(): string;
}

@type("OpenSmc.Data.EntireWorkspace")
export class EntireWorkspace extends WorkspaceReference {
    toJsonPath: () => "$";
}

// TODO: should ui be the one who resolves this from data store? (3/28/2024, akravets)
@type("OpenSmc.Data.LayoutAreaReference")
export class LayoutAreaReference extends WorkspaceReference  {
    constructor(public area: string) {
        super();
    }

    options: {}

    toJsonPath(): string {
        throw 'This reference should never be resolved on UI';
    }
}

@type("OpenSmc.Data.JsonPathReference")
export class JsonPathReference extends WorkspaceReference  {
    constructor(public path: string) {
        super();
    }

    toJsonPath() {
        return this.path;
    }
}

@type("Json.Patch.JsonPatch")
export class JsonPatch {
    constructor(public operations: PatchOperation[]) {
    }
}

export interface PatchOperation {
    op: "replace" | "remove" | "add";
    path: string;
    value?: any;
}

export type DataInput = {
    [key: string]: unknown | WorkspaceReference;
}