import { type } from "@open-smc/serialization/src/type";

@type("Json.Patch.JsonPatch")
export class JsonPatch {
    constructor(public operations: PatchOperation[]) {
    }
}

export interface PatchOperation {
    op: OperationType;
    path: string;
    value?: any;
}

export type OperationType = "replace" | "remove" | "add";