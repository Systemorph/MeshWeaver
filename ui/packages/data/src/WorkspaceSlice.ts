import { distinctUntilChanged, map, merge, skip, Subscription } from "rxjs";
import { Workspace } from "./Workspace";
import { selectByReference } from "./operators/selectByReference";
import { ValueOrReference } from "./contract/ValueOrReference";
import { extractReferences } from "./operators/extractReferences";
import { selectDeep } from "./operators/selectDeep";
import { selectByPath } from "./operators/selectByPath";
import { pathToUpdateAction } from "./operators/pathToUpdateAction";
import { referenceToUpdateAction } from "./operators/referenceToUpdateAction";

export class WorkspaceSlice<S, T> extends Workspace<T> {
    readonly subscription: Subscription;

    constructor(source: Workspace<S>, projection: ValueOrReference<T>, name?: string) {
        super(selectDeep(projection)(source.getState()), name);

        const references = extractReferences(projection);

        const source$ =
            merge(
                ...references
                    .map(
                        ([path, reference]) =>
                            source
                                .pipe(map(selectByReference(reference)))
                                .pipe(distinctUntilChanged())
                                .pipe(skip(1))
                                .pipe(map(pathToUpdateAction(path)))
                    )
            );

        const slice$ =
            merge(
                ...references
                    .map(
                        ([path, reference]) =>
                            this.store$
                                .pipe(map(selectByPath(path)))
                                .pipe(distinctUntilChanged())
                                .pipe(skip(1))
                                .pipe(map(referenceToUpdateAction(reference)))
                    )
            );

        this.subscription = new Subscription();

        this.subscription.add(source$.subscribe(this.store.dispatch));
        this.subscription.add(slice$.subscribe(source));
    }
}