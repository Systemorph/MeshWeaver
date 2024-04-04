import { Patch } from "immer";
import { PatchOperation } from "@open-smc/data/src/contract/JsonPatch";

export function toPatchOperation(patch: Patch): PatchOperation {
    const {op, path, value} = patch;

    return {
        op,
        path: "/" + path.join("/"),
        value
    }
}