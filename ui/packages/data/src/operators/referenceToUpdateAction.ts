import { WorkspaceReference } from "../contract/WorkspaceReference";
import { changeReference } from "../workspaceReducer";

export const referenceToUpdateAction = (reference: WorkspaceReference) =>
    (value: unknown) =>
        changeReference({
            reference,
            value
        });