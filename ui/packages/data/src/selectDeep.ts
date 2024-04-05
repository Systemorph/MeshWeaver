import { cloneDeepWith } from "lodash-es";
import { selectByReference } from "./selectByReference";
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { DataInput } from "./contract/DataInput";

export const selectDeep = <T extends DataInput>(data: T) =>
    (source: unknown): T =>
        cloneDeepWith(
            data,
            value =>
                value instanceof WorkspaceReference
                    ? (selectByReference(value)(source) ?? null)
                    : undefined
        );

