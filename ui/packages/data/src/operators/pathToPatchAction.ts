import { patchActionCreator } from "../workspaceReducer";
import { JsonPatch } from "../contract/JsonPatch";

export const pathToPatchAction = (path: string) =>
    (value: unknown) =>
        patchActionCreator(
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