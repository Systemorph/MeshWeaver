import { ValueOrReference } from "./contract/ValueOrReference";
import { WorkspaceSlice } from "./WorkspaceSlice";
import { Workspace } from "./Workspace";

export const sliceByReference = <T>(workspace: Workspace, projection: ValueOrReference<T>, name?: string) =>
    new WorkspaceSlice(workspace, projection, name);