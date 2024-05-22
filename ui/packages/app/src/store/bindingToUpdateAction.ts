import { Binding } from "@open-smc/data/src/contract/Binding";
import { updateByReferenceActionCreator } from "@open-smc/data/src/workspaceReducer";
import { JsonPathReference } from "@open-smc/data/src/contract/JsonPathReference";

export const bindingToUpdateAction = (binding: Binding) =>
    (value: unknown) =>
        updateByReferenceActionCreator({
            reference: new JsonPathReference(binding.path),
            value
        });