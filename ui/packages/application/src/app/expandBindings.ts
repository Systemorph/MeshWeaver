import { cloneDeepWith } from "lodash-es";
import { isOfType } from "../contract/ofType";
import { JsonPathReference } from "@open-smc/data/src/data.contract";
import { select } from "@open-smc/data/src/select";
import { Binding } from "../contract/Binding";

// TODO: respect parentDataContext (3/26/2024, akravets)
export const expandBindings = <T extends {}>(props: PropsInput<T>, parentDataContext: unknown) =>
    (source: unknown): T =>
        cloneDeepWith(
            props,
            value =>
                isOfType(value, Binding)
                    ? select(source, new JsonPathReference(value.path)) : undefined
        );

export type PropsInput<T extends {} = {}> = {
    [key: string]: unknown | Binding;
}