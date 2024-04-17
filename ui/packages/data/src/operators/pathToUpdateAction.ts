import { updateByReferenceActionCreator } from "../workspaceReducer";
import { PathReference } from "../contract/PathReference";

export const pathToUpdateAction = (path: string) =>
    (value: unknown) =>
        updateByReferenceActionCreator(
            {
                reference: new PathReference(path),
                value
            }
        );