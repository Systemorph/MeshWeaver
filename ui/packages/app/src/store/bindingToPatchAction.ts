import { Binding } from "@open-smc/layout/src/contract/Binding";
import { patch } from "@open-smc/data/src/workspaceReducer";
import { toPointer } from "@open-smc/data/src/toPointer";
import { JsonPatch } from "@open-smc/data/src/contract/JsonPatch";

export const bindingToPatchAction = (binding: Binding) =>
    (value: unknown) =>
        patch(
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