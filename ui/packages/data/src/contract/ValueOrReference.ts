import { WorkspaceReference } from "./WorkspaceReference";

export type ValueOrReference<T> = WorkspaceReference<T> | {
    [TKey in keyof T]: ValueOrReference<T[TKey]>;
}