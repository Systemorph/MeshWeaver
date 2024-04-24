import { createAction, createReducer } from '@reduxjs/toolkit';
import { JsonPatch, PatchOperation } from "./contract/JsonPatch";
import jsonPatch, { Operation } from "fast-json-patch";
import { applyPatches, enablePatches, Patch } from "immer";
import { identity } from "rxjs";

enablePatches();

export const jsonPatchActionCreator = createAction<JsonPatch>('patch');

export type JsonPatchAction = ReturnType<typeof jsonPatchActionCreator>;

export const patchRequest = createAction<JsonPatch>('patchRequest');

export const jsonPatchReducer = createReducer(
    undefined,
    builder => {
        builder
            .addCase(
                jsonPatchActionCreator,
                (state, action) =>
                    // TODO: use fast-json-patch (4/16/2024, akravets)
                    applyPatches(state, action.payload.operations.map(toImmerPatch))
            );
    }
);

function toImmerPatch(patch: Operation): Patch {
    const {op, path, value} = patch as PatchOperation;

    return {
        op,
        path: path?.split("/").filter(identity),
        value
    }
}