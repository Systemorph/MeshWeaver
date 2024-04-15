import { type } from "@open-smc/serialization/src/type";

@type("Json.Patch.JsonPatch")
export class JsonPatch {
    constructor(public operations: PatchOperation[]) {
    }

    // serialize() {
    //     return {...this};
    // }
    //
    // // keep raw operation values
    // static deserialize(props: JsonPatch) {
    //     const {operations} = props;
    //     return new JsonPatch(operations);
    // }
}

export interface PatchOperation {
    op: OperationType;
    path: string;
    value?: any;
}

export type OperationType = "replace" | "remove" | "add";