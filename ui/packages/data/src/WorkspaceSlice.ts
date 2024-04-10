import { distinctUntilChanged, map, merge, skip, Subscription } from "rxjs";
import { PatchAction } from "./workspaceReducer";
import { Workspace } from "./Workspace";
import { selectByReference } from "./operators/selectByReference";
import { ValueOrReference } from "./contract/ValueOrReference";
import { extractReferences } from "./operators/extractReferences";
import { isEqual } from "lodash-es";
import { pathToPatchAction } from "./operators/pathToPatchAction";
import { selectDeep } from "./operators/selectDeep";
import { selectByPath } from "./operators/selectByPath";
import { referenceToPatchAction } from "./operators/referenceToPatchAction";

export class WorkspaceSlice<S, P> extends Workspace<P> {
    readonly subscription: Subscription;

    constructor(workspace: Workspace<S>, projection: ValueOrReference<P>, name?: string) {
        super(selectDeep(projection)(workspace.getState()), name);

        const references = extractReferences(projection);

        const source$ =
            merge(
                ...references
                    .map(
                        ([path, reference]) =>
                            workspace
                                .pipe(map(selectByReference(reference)))
                                .pipe(distinctUntilChanged(isEqual))
                                .pipe(skip(1))
                                .pipe(map(pathToPatchAction(path)))
                    )
            );

        const slice$ =
            merge(
                ...references
                    .map(
                        ([path, reference]) =>
                            this.store$
                                .pipe(map(selectByPath(path)))
                                .pipe(distinctUntilChanged(isEqual))
                                .pipe(skip(1))
                                .pipe(map(referenceToPatchAction(reference)))
                    )
            );

        this.subscription = new Subscription();

        this.subscription.add(source$.subscribe(this.store.dispatch));
        this.subscription.add(slice$.subscribe(workspace));
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: PatchAction) {
        this.store.dispatch(value);
    }
}