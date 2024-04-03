import { Binding } from "@open-smc/layout/src/contract/Binding";
import { patch } from "@open-smc/data/src/workspaceReducer";
import { JsonPatch } from "@open-smc/data/src/data.contract";
import { toPointer } from "@open-smc/data/src/toPointer";

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