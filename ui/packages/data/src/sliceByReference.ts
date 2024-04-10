import { ValueOrReference } from "./contract/ValueOrReference";
import { WorkspaceSlice } from "./WorkspaceSlice";
import { Workspace } from "./Workspace";

export const sliceByReference = <S, T>(workspace: Workspace<S>, projection: ValueOrReference<T>, name?: string) =>
    new WorkspaceSlice(workspace, projection, name);