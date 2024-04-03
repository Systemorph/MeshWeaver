import { patchRequest } from "@open-smc/data/src/workspaceReducer";
import { toPointer } from "@open-smc/data/src/toPointer";
import { WorkspaceReference } from "@open-smc/data/src/contract/WorkspaceReference";
import { JsonPatch } from "@open-smc/data/src/contract/JsonPatch";

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