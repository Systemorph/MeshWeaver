import { createAction, createReducer } from '@reduxjs/toolkit';
import { applyPatches, enablePatches, Patch } from "immer";
import { identity } from "lodash-es";
import { JsonPatch, PatchOperation } from "./contract/JsonPatch";

enablePatches();

export const patchActionCreator = createAction<JsonPatch>('patch');

export type PatchAction = ReturnType<typeof patchActionCreator>;

export const patchRequest = createAction<JsonPatch>('patchRequest');

export const jsonPatchReducer = createReducer(
    undefined,
    builder => {
        builder
            .addCase(
                patchActionCreator,
                (state, action) =>
                    applyPatches(state, action.payload.operations.map(toImmerPatch))
            );
    }
);

function toImmerPatch(patch: PatchOperation): Patch {
    const {op, path, value} = patch;

    return {
        op,
        path: path?.split("/").filter(identity),
        value
    }
}