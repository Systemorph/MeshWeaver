import { createAction, createReducer, ThunkDispatch } from '@reduxjs/toolkit';
import { Draft, produce } from 'immer';

export const updateStoreActionCreator = createAction<unknown>('UPDATE_STORE');

export type UpdateStoreAction = ReturnType<typeof updateStoreActionCreator>;

export const updateStoreReducer = createReducer(
    undefined,
    builder => {
        builder
            .addCase(
                updateStoreActionCreator,
                (state, action) => {
                    return action.payload;
                }
            );
    }
);

export type UpdateStoreReducer<T, D = Draft<T>> = (draft: D) => D | void | undefined;
export type UpdateStoreRecipe<T> = T | UpdateStoreReducer<T>;

export function updateStore<T>(recipe: UpdateStoreRecipe<T>) {
    return (dispatch: ThunkDispatch<T, unknown, UpdateStoreAction>, getState: () => T) =>
        dispatch(
            recipe instanceof Function ?
                updateStoreActionCreator(
                    produce(getState(), recipe)
                )
                : updateStoreActionCreator(recipe)
        )
}