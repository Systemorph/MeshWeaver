import { configureStore, Store } from "@reduxjs/toolkit";
import { from, Observable, Observer, pipe } from "rxjs";
import { WorkspaceAction, workspaceReducer } from "./workspaceReducer";

export class Workspace<T = unknown> extends Observable<T> implements Observer<WorkspaceAction> {
    protected store: Store<T>;
    protected store$: Observable<T>;

    constructor(protected state?: T, public name?: string) {
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

    next(value: WorkspaceAction) {
        this.store.dispatch(value);
    }

    getState() {
        return this.store.getState();
    }
}