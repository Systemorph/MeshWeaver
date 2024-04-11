import { cloneDeepWith, get } from "lodash-es";
import { ValueOrReference } from "../contract/ValueOrReference";
import { WorkspaceReferenceBase } from "../contract/WorkspaceReferenceBase";

export const selectDeep = <T>(data: ValueOrReference<T>) =>
    (source: unknown): T =>
        cloneDeepWith(
            data,
            value =>
                value instanceof WorkspaceReferenceBase
                    ? (value.get(source) ?? null)
                    : undefined
        );

function select(path: string) {
        return <T>(source: T) => get(source, path)
}