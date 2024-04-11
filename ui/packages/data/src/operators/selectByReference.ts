import { WorkspaceReferenceBase } from "../contract/WorkspaceReferenceBase";

export const selectByReference = <T>(reference: WorkspaceReferenceBase<T>) =>
    (data: any) => reference.get(data)