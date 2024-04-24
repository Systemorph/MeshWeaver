import { Patch } from "immer";
import { PatchOperation } from "@open-smc/data/src/contract/JsonPatch.ts";
import { Operation } from "fast-json-patch";

export function toPatchOperation(patch: Patch): Operation {
    const {op, path, value} = patch;

    return {
        op,
        path: "/" + path.join("/"),
        value
    }
}