import { WorkspaceReference } from "./WorkspaceReference";

export type ValueOrReference<T = unknown> = WorkspaceReference |
    (T extends object ? {[TKey in keyof T]: ValueOrReference<T[TKey]>} : T);