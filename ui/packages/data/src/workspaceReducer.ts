import { createAction, createReducer } from '@reduxjs/toolkit';
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { JsonPatchAction, jsonPatchActionCreator, jsonPatchReducer } from "./jsonPatchReducer";
import { isPathReference } from "./operators/isPathReference";
import { getReferencePath } from "./operators/getReferencePath";
import { JsonPathReference } from "./contract/JsonPathReference";
import { toPointer } from "./toPointer";
import { updateByPath } from "./operators/updateByPath";

export type UpdateByReferencePayload<T = unknown> = {
    reference: WorkspaceReference<T>;
    value: T;
}

export const updateByReferenceActionCreator = createAction<UpdateByReferencePayload>('updateByReference');

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
            );
    }
);