import { patch } from "../workspaceReducer";
import { toPointer } from "../toPointer";
import { WorkspaceReference } from "../contract/WorkspaceReference";
import { JsonPatch } from "../contract/JsonPatch";

export const referenceToPatchAction = (reference: WorkspaceReference) =>
    (value: unknown) =>
        patch(
            new JsonPatch(
                [
                    {
                        op: "replace",
                        path: toPointer(reference.path),
                        value
                    }
                ]
            )
        );