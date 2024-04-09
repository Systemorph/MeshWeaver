import { Action } from "redux";
import { configureStore, Store } from "@reduxjs/toolkit";
import { from, Observable, Observer } from "rxjs";
import { workspaceReducer } from "./workspaceReducer";

export class Workspace<S> extends Observable<S> implements Observer<Action> {
    private readonly store: Store<S>;

    constructor(state?: S) {
        super(subscriber => from(this.store).subscribe(subscriber));

        this.store = configureStore<S>({
            preloadedState: state,
            reducer: workspaceReducer,
            devTools: false,
            middleware: getDefaultMiddleware =>
                getDefaultMiddleware({serializableCheck: false})
        });
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: Action): void {
        this.store.dispatch(value);
    }

    getState = () => this.store.getState();
}