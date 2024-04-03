import { configureStore } from "@reduxjs/toolkit"
import { Style } from "@open-smc/layout/src/contract/controls/Style";
import { appReducer } from "./appReducer";
import { from } from "rxjs";

export type AppState = {
    rootArea: string;
    areas: Record<string, LayoutAreaModel>;
}

export type LayoutAreaModel = {
    id: string;
    control?: ControlModel;
    options?: any;
    style?: Style;
}

export type ControlModel = {
    componentTypeName: string;
    props: { [prop: string]: unknown };
}

export const appStore = configureStore<AppState>({
    preloadedState: {
        rootArea: null,
        areas: {}
    },
    reducer: appReducer,
    devTools: {
        name: "app"
    }
});

export const app$ = from(appStore);

export type AppStore = typeof appStore;

export type AppDispatch = AppStore["dispatch"];

export const layoutAreaSelector = (id: string) => (state: AppState) => state.areas[id];