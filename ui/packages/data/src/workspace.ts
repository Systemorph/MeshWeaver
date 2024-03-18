import {
    configureStore,
    createAction,
    createReducer,
} from '@reduxjs/toolkit';
import { applyPatches, enablePatches, Patch, produce } from "immer";

enablePatches();

export const jsonPatches = createAction<Patch[]>('jsonPatches');
export const initState = createAction<any>('initState');

export function createWorkspace<TState>(initialState?: TState) {
    const reducer = createReducer(
        initialState,
        builder => {
            builder
                .addCase(
                    jsonPatches,
                    (state, action) => {
                        return applyPatches(state, action.payload);
                    }
                )
                .addCase(initState,(state, action) => action.payload);
        }
    );

    return configureStore({
        reducer
    });
}