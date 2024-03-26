import { DataInput, WorkspaceReference } from "./data.contract";
import { cloneDeepWith } from "lodash-es";
import { select } from "./select";

export const selectAll = <T>(data: DataInput<T>) =>
    (source: unknown) =>
        cloneDeepWith(
            data,
            value =>
                value instanceof WorkspaceReference
                    ? (select(source, value) ?? null)
                    : undefined
        );