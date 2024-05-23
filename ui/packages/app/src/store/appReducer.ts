import { createAction, createReducer } from "@reduxjs/toolkit";
import { AppState, LayoutAreaModel } from "./appStore";
import { UpdateStoreAction, updateStoreActionCreator, updateStoreReducer } from "@open-smc/data/src/updateStoreReducer";

export const setAreaActionCreator = createAction<LayoutAreaModel>('SET_AREA');
export const removeAreaActionCreator = createAction<string>('REMOVE_AREA');
export const setRootActionCreator = createAction<string>('SET_ROOT');

export type SetAreaAction = ReturnType<typeof setAreaActionCreator>;
export type RemoveAreaAction = ReturnType<typeof removeAreaActionCreator>;
export type SetRootAction = ReturnType<typeof setRootActionCreator>;

export type AppAction = SetAreaAction | RemoveAreaAction | SetRootAction | UpdateStoreAction;

export const appReducer = createReducer<AppState>(
    null,
    builder => {
        builder
            .addCase(setAreaActionCreator, (state, action) => {
                const layoutAreaModel = action.payload;
                state.areas[layoutAreaModel.area] = layoutAreaModel;
            })
            .addCase(removeAreaActionCreator, (state, action) => {
                delete state.areas[action.payload];
            })
            .addCase(setRootActionCreator, (state, action) => {
                state.rootArea = action.payload;
            })
            .addCase(
                updateStoreActionCreator,
                updateStoreReducer
            )
    }
);