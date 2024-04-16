import { changeReference } from "../workspaceReducer";
import { PathReference } from "../contract/PathReference";

export const pathToUpdateAction = (path: string) =>
    (value: unknown) =>
        changeReference(
            {
                reference: new PathReference(path),
                value
            }
        );