import { Workspace } from "./Workspace";
import { WorkspaceSlice } from "./WorkspaceSlice";
import { WorkspaceReference } from "./contract/WorkspaceReference";

export const sliceByPath = <S, T>(workspace: Workspace<S>, path: Paths<S>) =>
    new WorkspaceSlice(workspace, new WorkspaceReference<T>(path), `${workspace.name}${path}`);

type Paths<T> =
    T extends object ? {
            [K in keyof T]:
            `/${Exclude<K, symbol>}${"" | `${Paths<T[K]>}`}`
        }[keyof T]
        : never;