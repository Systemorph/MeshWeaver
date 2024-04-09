import { WorkspaceReference } from "./contract/WorkspaceReference";
import { map } from "rxjs";
import { selectByReference } from "./operators/selectByReference";
import { setState } from "./workspaceReducer";
import { referenceToPatchAction } from "./operators/referenceToPatchAction";
import { Workspace } from "./Workspace";

export function slice<S, T>(workspace: Workspace<S>, reference: WorkspaceReference<T>) {
    const slice = new Workspace<S>();

    workspace
        .pipe(map(selectByReference(reference)))
        .pipe(map(setState))
        .subscribe(slice);

    slice
        .pipe(map(referenceToPatchAction(reference)))
        .subscribe(workspace);

    return slice;
}