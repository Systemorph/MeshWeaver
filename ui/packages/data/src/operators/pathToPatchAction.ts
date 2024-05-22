import { jsonPatchActionCreator } from "../jsonPatchReducer";
import { JsonPatch } from "../contract/JsonPatch";

export const pathToPatchAction = (path: string) =>
    (value: unknown) =>
        jsonPatchActionCreator(
            new JsonPatch(
                [
                    {
                        op: "replace",
                        path,
                        value
                    }
                ]
            )
        );