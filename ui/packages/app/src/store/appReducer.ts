import { createAction, createReducer } from "@reduxjs/toolkit";
import { AppState, LayoutAreaModel } from "./appStore";

export interface SetPropAction {
    areaId: string;
    prop: string;
    value: any;
}

export const setProp = createAction<SetPropAction>('setProp');
export const setArea = createAction<LayoutAreaModel>('setArea');
export const removeArea = createAction<string>('removeArea');
export const setRoot = createAction<string>('setRoot');

export type SetProp = ReturnType<typeof setProp>;
export type SetArea = ReturnType<typeof setArea>;
export type RemoveArea = ReturnType<typeof removeArea>;
export type SetRoot = ReturnType<typeof setRoot>;

export type AppAction = SetProp | SetArea | RemoveArea | SetRoot;

export const appReducer = createReducer<AppState>(
    null,
    builder => {
        builder
            .addCase(setProp, (state, action) => {
                const {areaId, prop, value} = action.payload;
                (state.areas[areaId].control.props as any)[prop] = value;
            })
            .addCase(setArea, (state, action) => {
                const area = action.payload;
                state.areas[area.area] = area;
            })
            .addCase(removeArea, (state, action) => {
                delete state.areas[action.payload];
            })
            .addCase(setRoot, (state, action) => {
                state.rootArea = action.payload;
            })
    }
);