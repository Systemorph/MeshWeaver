import { JsonPatch, OperationType } from "../contract/JsonPatch";
import { jsonPatchActionCreator } from "../jsonPatchReducer";

export const toPatch = (path: string, op: OperationType) =>
    (value: unknown) =>
        jsonPatchActionCreator(
            new JsonPatch([
                {op, path, value}
            ])
        );

