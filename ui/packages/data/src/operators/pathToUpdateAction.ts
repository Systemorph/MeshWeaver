import { updateByReferenceActionCreator } from "../workspaceReducer";
import { WorkspaceReference } from "../contract/WorkspaceReference";

export const pathToUpdateAction = (path: string) =>
    (value: unknown) =>
        updateByReferenceActionCreator(
            {
                reference: new WorkspaceReference(path),
                value
            }
        );