import { Workspace } from "./Workspace";
import { WorkspaceSlice } from "./WorkspaceSlice";
import { PathReference } from "./contract/PathReference";

export const sliceByPath = <S, T>(workspace: Workspace<S>, path: Paths<S>) =>
    new WorkspaceSlice<T>(workspace, new PathReference(path));

type Paths<T> =
    T extends object ? {
            [K in keyof T]:
            `/${Exclude<K, symbol>}${"" | `${Paths<T[K]>}`}`
        }[keyof T]
        : never;