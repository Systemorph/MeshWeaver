import { cloneDeepWith, get } from "lodash-es";
import { ValueOrReference } from "../contract/ValueOrReference";
import { WorkspaceReference } from "../contract/WorkspaceReference";

export const selectDeep = <T>(data: ValueOrReference<T>) =>
    (source: unknown): T =>
        cloneDeepWith(
            data,
            value =>
                value instanceof WorkspaceReference
                    ? (value.get(source) ?? null)
                    : undefined
        );

function select(path: string) {
        return <T>(source: T) => get(source, path)
}