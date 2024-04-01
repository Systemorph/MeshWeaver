import { DataInput, WorkspaceReference } from "./data.contract";
import { cloneDeepWith } from "lodash-es";
import { selectByReference } from "./selectByReference";

export const selectDeep = <T extends DataInput>(data: T) =>
    (source: unknown): T =>
        cloneDeepWith(
            data,
            value =>
                value instanceof WorkspaceReference
                    ? (selectByReference(source, value) ?? null)
                    : undefined
        );