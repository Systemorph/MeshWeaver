import { Binding } from "@open-smc/layout/src/contract/Binding";
import { jsonPatch } from "@open-smc/data/src/workspaceReducer";
import { JsonPatch } from "@open-smc/data/src/data.contract";
import { toPointer } from "@open-smc/data/src/toPointer";

export const bindingToPatchAction = (binding: Binding) =>
    (value: unknown) =>
        jsonPatch(
            new JsonPatch(
                [
                    {
                        op: "replace",
                        path: toPointer(binding.path),
                        value
                    }
                ]
            )
        );