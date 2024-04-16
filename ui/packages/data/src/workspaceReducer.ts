import { createAction, createReducer } from '@reduxjs/toolkit';
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { JsonPatchAction, jsonPatchActionCreator, jsonPatchReducer } from "./jsonPatchReducer";
import { isPathReference } from "./operators/isPathReference";
import { isEmpty, set } from "lodash-es";
import { getReferencePath } from "./operators/getReferencePath";
import { JsonPathReference } from "./contract/JsonPathReference";
import { pointerToArray } from "./operators/pointerToArray";

export type ChangeReferencePayload<T = unknown> = {
    reference: WorkspaceReference<T>;
    value: T;
}

export const changeReference = createAction<ChangeReferencePayload>('update');

export type ChangeReferenceAction = ReturnType<typeof changeReference>;

export type WorkspaceAction = ChangeReferenceAction | JsonPatchAction;

export const workspaceReducer = createReducer(
    undefined,
    builder => {
        builder
            .addCase(
                changeReference,
                (state, action) => {
                    const {reference, value} = action.payload;
                    if (isPathReference(reference)) {
                        const path = getReferencePath(reference);
                        if (path === "") {
                            return action.payload.value;
                        }
                        else {
                            set(state, pointerToArray(path), value);
                        }
                    }
                    else if (reference instanceof JsonPathReference) {
                        // TODO: not implemented (4/16/2024, akravets)
                        console.warn("update by json path ref not implemented")
                    }
                }
            )
            .addCase(
                jsonPatchActionCreator,
                jsonPatchReducer
            );
    }
);