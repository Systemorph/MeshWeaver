import { patch } from "../workspaceReducer";
import { JsonPatch } from "../contract/JsonPatch";

export const pathToPatchAction = (path: string) =>
    (value: unknown) =>
        patch(
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