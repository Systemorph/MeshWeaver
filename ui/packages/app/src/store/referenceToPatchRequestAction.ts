import { JsonPatch, WorkspaceReference } from "@open-smc/data/src/data.contract";
import { patchRequest } from "@open-smc/data/src/workspaceReducer";
import { toPointer } from "@open-smc/data/src/toPointer";

export const referenceToPatchRequestAction = (reference: WorkspaceReference) =>
    (value: unknown) =>
        patchRequest(
            new JsonPatch(
                [
                    {
                        op: "replace",
                        path: toPointer(reference.toJsonPath()),
                        value
                    }
                ]
            )
        );