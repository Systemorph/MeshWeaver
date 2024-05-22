import { WorkspaceReference } from "../contract/WorkspaceReference";
import { updateByReferenceActionCreator } from "../workspaceReducer";

export const referenceToUpdateAction = (reference: WorkspaceReference) =>
    (value: unknown) =>
        updateByReferenceActionCreator({
            reference,
            value
        });