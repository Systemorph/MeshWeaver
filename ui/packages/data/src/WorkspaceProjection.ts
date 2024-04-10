import { map, Observable, Observer } from "rxjs";
import { PatchAction, patchActionCreator } from "./workspaceReducer";
import { Workspace } from "./Workspace";
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { selectByReference } from "./operators/selectByReference";
import { JsonPatch } from "./contract/JsonPatch";

export class WorkspaceProjection<T, P> extends Observable<P> implements Observer<PatchAction> {
    constructor(private workspace: Workspace<T>, private reference: WorkspaceReference<P>) {
        super(
            subscriber =>
                workspace
                    .pipe(
                        map(
                            selectByReference(reference)
                        )
                    )
                    .subscribe(subscriber)
        );
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: PatchAction) {
        this.workspace.next(mapPatch(value, this.reference));
    }
}

function mapPatch(patchAction: PatchAction, reference: WorkspaceReference) {
    const {payload} = patchAction;

    const mappedOperations =
        payload.operations.map(({op, path, value}) => ({
            op,
            path: reference.path + path,
            value
        }));

    return patchActionCreator(new JsonPatch(mappedOperations));
}