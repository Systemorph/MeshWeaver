import { WorkspaceReferenceBase } from "../contract/WorkspaceReferenceBase";
import { updateByReferenceActionCreator } from "../workspaceReducer";

export const referenceToUpdateAction = (reference: WorkspaceReferenceBase) =>
    (value: unknown) =>
        updateByReferenceActionCreator({
            reference,
            value
        });