import { createAction, createReducer, ThunkAction, ThunkDispatch, UnknownAction } from '@reduxjs/toolkit';
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { JsonPatchAction, jsonPatchActionCreator, jsonPatchReducer } from "./jsonPatchReducer";
import { isPathReference } from "./operators/isPathReference";
import { getReferencePath } from "./operators/getReferencePath";
import { JsonPathReference } from "./contract/JsonPathReference";
import { toPointer } from "./toPointer";
import { updateByPath } from "./operators/updateByPath";
import { UpdateStoreAction, updateStoreActionCreator, updateStoreReducer } from './updateStoreReducer';

export type UpdateByReferencePayload<T = unknown> = {
    reference: WorkspaceReference;
    value: T;
}

export const updateByReferenceActionCreator = createAction<UpdateByReferencePayload>('UPDATE_BY_REFERENCE');

export type UpdateByReferenceAction = ReturnType<typeof updateByReferenceActionCreator>;

export type WorkspaceThunk<State, ReturnType = void> =
    ThunkAction<
        ReturnType,
        State,
        unknown,
        WorkspaceAction
    >


export type WorkspaceAction = UpdateByReferenceAction | JsonPatchAction | UpdateStoreAction;

export const workspaceReducer = createReducer(
    undefined,
    builder => {
        builder
            .addCase(
                updateByReferenceActionCreator,
                (state, action) => {
                    const { reference, value } = action.payload;
                    if (isPathReference(reference)) {
                        const path = getReferencePath(reference);

                        if (path === "") {
                            return action.payload.value;
                        }
                        else {
                            updateByPath(state, path, value);
                        }
                    }
                    else if (reference instanceof JsonPathReference) {
                        try {
                            const path = toPointer(reference.path);

                            if (path === "") {
                                return action.payload.value;
                            }
                            else {
                                updateByPath(state, path, value);
                            }
                        }
                        catch (error) {
                            console.warn(`Update by jsonPath "${reference.path} failed`);
                        }
                    }
                }
            )
            .addCase(
                jsonPatchActionCreator,
                jsonPatchReducer
            )
            .addCase(
                updateStoreActionCreator,
                updateStoreReducer
            )
    }
);