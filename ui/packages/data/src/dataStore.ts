import { configureStore } from "@reduxjs/toolkit";
import { workspaceReducer } from "./workspaceReducer";
import { from } from "rxjs";
import { serializeMiddleware } from "./serializeMiddleware";

export const dataStore =
    configureStore({
        reducer: workspaceReducer,
        devTools: {
            name: "data"
        },
        middleware: getDefaultMiddleware =>
            getDefaultMiddleware()
                .prepend(serializeMiddleware),
    });

export const data$ =
    from(dataStore);