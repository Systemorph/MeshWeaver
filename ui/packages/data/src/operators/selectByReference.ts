import { WorkspaceReference } from "../contract/WorkspaceReference";

export const selectByReference = <T>(reference: WorkspaceReference<T>) =>
    (data: any) => reference.get(data)