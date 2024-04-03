import { JsonPatch, WorkspaceReference } from "@open-smc/data/src/data.contract";
import { jsonPatch } from "@open-smc/data/src/workspaceReducer";
import { toPointer } from "@open-smc/data/src/toPointer";

export const referenceToPatchAction = (reference: WorkspaceReference) =>
    (value: unknown) =>
        jsonPatch(
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