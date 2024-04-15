import { configureStore, Store } from "@reduxjs/toolkit";
import { from, Observable, Observer } from "rxjs";
import { JsonPatchAction, jsonPatchReducer } from "./jsonPatchReducer";
import { serializeMiddleware } from "./middleware/serializeMiddleware";

export class ObjectStore<T = unknown> extends Observable<T> implements Observer<JsonPatchAction> {
    protected store: Store;
    protected store$: Observable<T>;

    constructor(protected state: T, public name?: string) {
        super(subscriber => this.store$.subscribe(subscriber));

        this.store = configureStore({
            preloadedState: state,
            reducer: jsonPatchReducer,
            devTools: name ? {name} : false,
            middleware: getDefaultMiddleware =>
                getDefaultMiddleware()
                    .prepend(serializeMiddleware)
        });

        this.store$ = from(this.store);
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: JsonPatchAction) {
        this.store.dispatch(value);
    }

    getState() {
        return this.store.getState();
    }
}