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

export class WorkspaceReference {}

@contractMessage("OpenSmc.Data")
export class EntireWorkspace extends WorkspaceReference {
}

@contractMessage("OpenSmc.Data")
export class LayoutAreaReference extends WorkspaceReference  {
    constructor(public path: string) {
        super();
    }
}

@contractMessage("OpenSmc.Data")
export class JsonPathReference extends WorkspaceReference  {
    constructor(public path: string) {
        super();
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

export type DataInput<T> = {
    [key: string]: unknown | WorkspaceReference;
}