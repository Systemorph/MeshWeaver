import { Workspace } from "./Workspace";
import { WorkspaceSlice } from "./WorkspaceSlice";
import { PathReference } from "./contract/PathReferenceBase";

export const sliceByPath = <S, T>(workspace: Workspace<S>, path: Paths<S>) =>
    new WorkspaceSlice(workspace, new PathReference<T>(path), `${workspace.name}${path}`);

type Paths<T> =
    T extends object ? {
            [K in keyof T]:
            `/${Exclude<K, symbol>}${"" | `${Paths<T[K]>}`}`
        }[keyof T]
        : never;