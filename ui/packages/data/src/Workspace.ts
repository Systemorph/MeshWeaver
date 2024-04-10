import { Action } from "redux";
import { configureStore, Store } from "@reduxjs/toolkit";
import { from, Observable, Observer } from "rxjs";
import { PatchAction, workspaceReducer } from "./workspaceReducer";
import { ValueOrReference } from "./contract/ValueOrReference";
import { WorkspaceSlice } from "./WorkspaceSlice";
import { WorkspaceReference } from "./contract/WorkspaceReference";

export class Workspace<S> extends Observable<S> implements Observer<PatchAction> {
    protected store: Store<S>;
    protected store$: Observable<S>;

    constructor(protected state: S, public name?: string) {
        super(subscriber => this.store$.subscribe(subscriber));

        this.store = configureStore({
            preloadedState: state,
            reducer: workspaceReducer,
            devTools: name ? {name} : false,
            middleware: getDefaultMiddleware =>
                getDefaultMiddleware({serializableCheck: false})
        });

        this.store$ = from(this.store);
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
}