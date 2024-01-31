import { createStore } from "@open-smc/store/store";

export const makeAppStore = () => createStore<AppState>({});

export type AppState = {
    readonly block?: boolean;
}

export type AppStore = ReturnType<typeof makeAppStore>;