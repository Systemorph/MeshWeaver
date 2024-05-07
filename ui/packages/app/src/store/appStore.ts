import { configureStore } from "@reduxjs/toolkit"
import { appReducer } from "./appReducer";
import { from, Subject } from "rxjs";
import { ControlView } from "../ControlDef";

export type AppState = {
    rootArea: string;
    areas: Record<string, LayoutAreaModel>;
}

export type LayoutAreaModel<T extends ControlView = unknown> = {
    area: string;
    controlName: string;
    props: T
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

export const appMessage$ = new Subject();