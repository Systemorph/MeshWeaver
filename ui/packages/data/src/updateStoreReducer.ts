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

export function updateStore<T>(reducer: (state: Draft<T>) => Draft<T> | void) {
    return (dispatch: ThunkDispatch<T, unknown, UpdateStoreAction>, getState: () => T) =>
        dispatch(
            updateStoreActionCreator(
                produce(getState(), reducer)
            )
        )
}