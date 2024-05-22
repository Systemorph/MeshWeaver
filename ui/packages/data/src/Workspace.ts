import { configureStore } from "@reduxjs/toolkit";
import { from, Observable, Observer } from "rxjs";
import { WorkspaceAction, workspaceReducer, WorkspaceThunk } from "./workspaceReducer";
import { updateStore, UpdateStoreRecipe } from "./updateStoreReducer";

export class Workspace<T = unknown> extends Observable<T> implements Observer<WorkspaceAction> {
    protected store: ReturnType<typeof configureStore<T, WorkspaceAction>>;
    protected store$: Observable<T>;

    constructor(state?: T, public name?: string) {
        super(subscriber => this.store$.subscribe(subscriber));

        this.store = configureStore<T, WorkspaceAction>({
            preloadedState: state,
            reducer: workspaceReducer,
            devTools: name ? { name } : false,
            middleware: getDefaultMiddleware =>
                getDefaultMiddleware(
                    {
                        thunk: true,
                        serializableCheck: false
                    }
                )
        });

        this.store$ = from(this.store);
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: WorkspaceAction | WorkspaceThunk<T>) {
        this.store.dispatch(value);
    }

    getState() {
        return this.store.getState();
    }

    update(reducer: UpdateStoreRecipe<T>) {
        this.store.dispatch(updateStore<T>(reducer));
    }
}