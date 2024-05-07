import { createAction, createReducer } from "@reduxjs/toolkit";
import { AppState, LayoutAreaModel } from "./appStore";

export const updateAreaActionCreator = createAction<Partial<LayoutAreaModel>>('updateArea');
export const setArea = createAction<LayoutAreaModel>('setArea');
export const removeArea = createAction<string>('removeArea');
export const setRoot = createAction<string>('setRoot');

export type UpdateArea = ReturnType<typeof updateAreaActionCreator>;
export type SetArea = ReturnType<typeof setArea>;
export type RemoveArea = ReturnType<typeof removeArea>;
export type SetRoot = ReturnType<typeof setRoot>;

export type AppAction = UpdateArea | SetArea | RemoveArea | SetRoot;

export const appReducer = createReducer<AppState>(
    null,
    builder => {
        builder
            .addCase(updateAreaActionCreator, (state, action) => {
                const layoutAreaModel = action.payload as LayoutAreaModel;
                if (!state.areas[layoutAreaModel.area]) {
                    throw 'Area not found';
                }
                Object.assign(state.areas[layoutAreaModel.area], layoutAreaModel);
            })
            .addCase(setArea, (state, action) => {
                const layoutAreaModel = action.payload;
                state.areas[layoutAreaModel.area] = layoutAreaModel;
            })
            .addCase(removeArea, (state, action) => {
                delete state.areas[action.payload];
            })
            .addCase(setRoot, (state, action) => {
                state.rootArea = action.payload;
            })
    }
);