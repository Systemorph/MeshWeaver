import { contractMessage } from "@open-smc/utils/src/contractMessage";
import { LayoutArea } from "@open-smc/application/src/contract/application.contract";

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

@contractMessage("OpenSmc.Data.DataChangedEvent")
export class DataChangedEvent {
    constructor(public change: object) {
    }
}

@contractMessage("OpenSmc.Data.EntireWorkspace")
export class EntireWorkspace {
}

@contractMessage("OpenSmc.Data.LayoutAreaReference")
export class LayoutAreaReference {
    constructor(public path: string) {
    }
}

@contractMessage("OpenSmc.Data.JsonPathReference")
export class JsonPathReference {
    constructor(public path: string) {
    }
}

@contractMessage("Json.Patch.JsonPatch")
export class JsonPatch {
    operations: PatchOperation[];
}

export interface PatchOperation {
    op: "replace" | "remove" | "add";
    path: string;
    value?: any;
}