import { configureStore } from "@reduxjs/toolkit";
import { workspaceReducer } from "./workspaceReducer";
import { from, map } from "rxjs";
import { deserialize } from "@open-smc/serialization/src/deserialize";
import { serializeMiddleware } from "./serializeMiddleware";

export const dataStore =
    configureStore({
        reducer: workspaceReducer,
        devTools: {
            name: "data"
        },
        middleware: getDefaultMiddleware =>
            getDefaultMiddleware().concat(serializeMiddleware),
    });

export const data$ =
    from(dataStore)
        .pipe(map(deserialize));