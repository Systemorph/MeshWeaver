import { Patch } from "immer";
import { Operation } from "fast-json-patch";

export function convertImmerPatchOperation(patch: Patch): Operation {
    const {op, path, value} = patch;

    return {
        op,
        path: "/" + path.join("/"),
        value
    }
}