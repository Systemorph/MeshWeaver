import { cloneDeepWith } from "lodash-es";
import { WorkspaceReference } from "../contract/WorkspaceReference";
import { ValueOrReference } from "../contract/ValueOrReference";

export const selectDeep = <T>(data: ValueOrReference<T>) =>
    (source: unknown): T =>
        cloneDeepWith(
            data,
            value =>
                value instanceof WorkspaceReference
                    ? (value.get(source) ?? null)
                    : undefined
        );

