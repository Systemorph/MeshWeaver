import { Action } from "redux";
import { configureStore, Store } from "@reduxjs/toolkit";
import { distinctUntilChanged, from, map, merge, Observable, Observer, skip, Subscription } from "rxjs";
import { workspaceReducer } from "./workspaceReducer";
import { ValueOrReference } from "./contract/ValueOrReference";
import { extractReferences } from "./operators/extractReferences";
import { selectByPath } from "./operators/selectByPath";
import { isEqual } from "lodash-es";
import { referenceToPatchAction } from "./operators/referenceToPatchAction";
import { selectByReference } from "./operators/selectByReference";
import { selectDeep } from "./operators/selectDeep";
import { pathToPatchAction } from "./operators/pathToPatchAction";

export class Workspace<S> extends Observable<S> implements Observer<Action> {
    private readonly store: Store<S>;

    constructor(state?: S, name?: string) {
        super(subscriber => from(this.store).subscribe(subscriber));

        this.store = configureStore<S>({
            preloadedState: state,
            reducer: workspaceReducer,
            devTools: name ? {name} : false,
            middleware: getDefaultMiddleware =>
                getDefaultMiddleware({serializableCheck: false})
        });
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: Action) {
        this.store.dispatch(value);
    }

    getState() {
        return this.store.getState();
    }

    map<T>(projection: ValueOrReference<T>, name?: string) {
        const state = selectDeep(projection)(this.getState());

        const workspace = new Workspace(state, name);

        const subscription = new Subscription();

        const references = extractReferences(projection);

        const patchChild$ =
            merge(
                ...references
                    .map(
                        ([path, reference]) =>
                            this
                                .pipe(map(selectByReference(reference)))
                                .pipe(distinctUntilChanged(isEqual))
                                .pipe(skip(1))
                                .pipe(map(pathToPatchAction(path)))
                    )
            );

        const patchParent$ =
            merge(
                ...references
                    .map(
                        ([path, reference]) =>
                            workspace
                                .pipe(map(selectByPath(path)))
                                .pipe(distinctUntilChanged(isEqual))
                                .pipe(skip(1))
                                .pipe(map(referenceToPatchAction(reference)))
                    )
            );

        subscription.add(patchChild$.subscribe(workspace));

        subscription.add(patchParent$.subscribe(this));

        return [workspace, subscription] as const;
    }
}