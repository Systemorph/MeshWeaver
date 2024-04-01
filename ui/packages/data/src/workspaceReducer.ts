import {
    createAction,
    createReducer
} from '@reduxjs/toolkit';
import { applyPatches, enablePatches, Patch } from "immer";
import { JsonPatch, PatchOperation } from "./data.contract";
import { identity } from "lodash-es";

enablePatches();

export const jsonPatch = createAction<JsonPatch>('jsonPatch');
export const initState = createAction<unknown>('initState');

export const workspaceReducer = createReducer(
    undefined,
    builder => {
        builder
            .addCase(
                jsonPatch,
                (state, action) =>
                    action.payload.operations &&
                    applyPatches(state, action.payload.operations.map(toImmerPatch))
            )
            .addCase(
                initState,
                (state, action) => action.payload
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