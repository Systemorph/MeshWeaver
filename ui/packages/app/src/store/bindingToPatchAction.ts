import { Binding } from "@open-smc/layout/src/contract/Binding";
import { toPointer } from "@open-smc/data/src/toPointer";
import { pathToPatchAction } from "@open-smc/data/src/operators/pathToPatchAction";

export const bindingToPatchAction = (binding: Binding) =>
    pathToPatchAction(toPointer(binding.path));