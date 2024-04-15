import { createAction, createReducer } from '@reduxjs/toolkit';
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { JsonPatchAction, jsonPatchActionCreator, jsonPatchReducer } from "./jsonPatchReducer";

export type UpdateByReferencePayload<T = unknown> = {
    reference: WorkspaceReference<T>;
    value: T;
}

export const updateByReferenceActionCreator = createAction<UpdateByReferencePayload>('update');

export type UpdateByReferenceAction = ReturnType<typeof updateByReferenceActionCreator>;

export type WorkspaceAction = UpdateByReferenceAction | JsonPatchAction;

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
            )
            .addCase(
                jsonPatchActionCreator,
                jsonPatchReducer
            );
    }
);