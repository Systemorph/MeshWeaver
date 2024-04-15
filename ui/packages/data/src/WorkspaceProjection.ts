import { map, Observable, Observer } from "rxjs";
import { JsonPatchAction, jsonPatchActionCreator } from "./jsonPatchReducer";
import { Workspace } from "./Workspace";
import { PathReferenceBase } from "./contract/PathReferenceBase";
import { selectByReference } from "./operators/selectByReference";
import { JsonPatch } from "./contract/JsonPatch";

export class WorkspaceProjection<T, P> extends Observable<P> implements Observer<JsonPatchAction> {
    constructor(private workspace: Workspace<T>, private reference: PathReferenceBase<P>) {
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

    next(value: JsonPatchAction) {
        this.workspace.next(mapPatch(value, this.reference));
    }
}

function mapPatch(patchAction: JsonPatchAction, reference: PathReferenceBase) {
    const {payload} = patchAction;

    const mappedOperations =
        payload.operations.map(({op, path, value}) => ({
            op,
            path: reference.path + path,
            value
        }));

    return jsonPatchActionCreator(new JsonPatch(mappedOperations));
}