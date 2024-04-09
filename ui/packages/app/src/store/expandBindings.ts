import { cloneDeepWith } from "lodash-es";
import { Binding } from "@open-smc/layout/src/contract/Binding";
import { selectByReference } from "@open-smc/data/src/operators/selectByReference";
import { JsonPathReference } from "@open-smc/data/src/contract/JsonPathReference";

// TODO: respect parentDataContext (3/26/2024, akravets)
export const expandBindings = <T>(props: T, parentDataContext?: unknown) =>
    (source: unknown): T =>
        cloneDeepWith(
            props,
            value =>
                value instanceof Binding
                    ? selectByReference(new JsonPathReference(value.path))(source) ?? null : undefined
        );