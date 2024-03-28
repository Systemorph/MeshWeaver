import { contractMessage } from "@open-smc/utils/src/contractMessage";

@contractMessage("OpenSmc.Data.SubscribeDataRequest")
export class SubscribeDataRequest {
    constructor(public id: string, public workspaceReference: unknown) {
    }
}

@contractMessage("OpenSmc.Data.UnsubscribeDataRequest")
export class UnsubscribeDataRequest {
    constructor(public ids: string[]) {
    }
}

@contractMessage("OpenSmc.Data")
export class DataChangedEvent {
    constructor(public id: string, public change: object) {
    }
}

export abstract class WorkspaceReference {
    abstract toJsonPath(): string;
}

@contractMessage("OpenSmc.Data")
export class EntireWorkspace extends WorkspaceReference {
    toJsonPath: () => "$";
}

// TODO: should ui be the one who resolves this from data store? (3/28/2024, akravets)
@contractMessage("OpenSmc.Data")
export class LayoutAreaReference extends WorkspaceReference  {
    constructor(public path: string) {
        super();
    }

    toJsonPath(): string {
        throw 'This reference should never be resolved on UI';
    }
}

@contractMessage("OpenSmc.Data")
export class JsonPathReference extends WorkspaceReference  {
    constructor(public path: string) {
        super();
    }

    toJsonPath() {
        return this.path;
    }
}

@contractMessage("Json.Patch.JsonPatch")
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