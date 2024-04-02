import { cloneDeepWith } from "lodash-es";
import { JsonPathReference } from "@open-smc/data/src/data.contract";
import { Binding } from "@open-smc/layout/src/contract/Binding";
import { selectByReference } from "@open-smc/data/src/selectByReference";

// TODO: respect parentDataContext (3/26/2024, akravets)
export const expandBindings = <T>(props: T, parentDataContext?: unknown) =>
    (source: unknown): T =>
        cloneDeepWith(
            props,
            value =>
                value instanceof Binding
                    ? selectByReference(source, new JsonPathReference(value.path)) ?? null : undefined
        );