import { type } from "@open-smc/serialization/src/type";

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