import { JsonPatch, OperationType } from "../contract/JsonPatch";
import { patchActionCreator } from "../jsonPatchReducer";

export const toPatch = (path: string, op: OperationType) =>
    (value: unknown) =>
        patchActionCreator(
            new JsonPatch([
                {op, path, value}
            ])
        );

