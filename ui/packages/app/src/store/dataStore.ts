import { configureStore } from "@reduxjs/toolkit";
import { workspaceReducer } from "@open-smc/data/src/workspaceReducer";
import { from } from "rxjs";
import { serializeMiddleware } from "@open-smc/data/src/middleware/serializeMiddleware";
import { sampleApp } from "@open-smc/backend/src/SampleApp";
import { patchRequestMiddleware } from "./middleware/patchRequestMiddleware";

export const dataStore =
    configureStore({
        reducer: workspaceReducer,
        devTools: {
            name: "data"
        },
        middleware: getDefaultMiddleware =>
            getDefaultMiddleware()
                .prepend(
                    patchRequestMiddleware(sampleApp),
                    serializeMiddleware
                )
    });

export const data$ =
    from(dataStore);