import {
    configureStore,
    createAction,
    createReducer, Store,
} from '@reduxjs/toolkit';
import { applyPatches, enablePatches, Patch } from "immer";
import { JsonPatch, PatchOperation, WorkspaceReference } from "./data.contract";

enablePatches();

export const jsonPatch = createAction<JsonPatch>('jsonPatch');
export const initState = createAction<any>('initState');

export function createWorkspace<TState>(initialState?: TState, name?: string) {
    const reducer = createReducer(
        initialState,
        builder => {
            builder
                .addCase(
                    jsonPatch,
                    (state, action) =>
                        action.payload.operations && applyPatches(state, action.payload.operations.map(toImmerPatch))
                )
                .addCase(
                    initState,
                    (state, action) => action.payload
                );
        }
    );

    return configureStore({
        reducer,
        devTools: {
            name
        }
    });
}

function reduce(store: Store, reference: WorkspaceReference) {

}

function toImmerPatch(patch: PatchOperation): Patch {
    const {op, path, value} = patch;

    return {
        op,
        path: path?.split("/"),
        value
    }
}