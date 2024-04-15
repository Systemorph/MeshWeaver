import { updateByReferenceActionCreator } from "../workspaceReducer";
import { PathReferenceBase } from "../contract/PathReferenceBase";

export const pathToUpdateAction = (path: string) =>
    (value: unknown) =>
        updateByReferenceActionCreator(
            {
                reference: new PathReferenceBase(path),
                value
            }
        );