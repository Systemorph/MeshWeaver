import { createAction, createReducer } from '@reduxjs/toolkit';
import { WorkspaceReferenceBase } from "./contract/WorkspaceReferenceBase";

export type UpdateByReferencePayload<T = unknown> = {
    reference: WorkspaceReferenceBase<T>;
    value: T;
}

export const updateByReferenceActionCreator = createAction<UpdateByReferencePayload>('update');

export type UpdateByReferenceAction = ReturnType<typeof updateByReferenceActionCreator>;

export type WorkspaceAction = UpdateByReferenceAction;

export const workspaceReducer = createReducer(
    undefined,
    builder => {
        builder
            .addCase(
                updateByReferenceActionCreator,
                (state, action) => {
                    const {reference, value} = action.payload;
                    reference.set(state, value);
                }
            );
    }
);