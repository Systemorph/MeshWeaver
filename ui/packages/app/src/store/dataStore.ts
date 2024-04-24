import { configureStore } from "@reduxjs/toolkit";
import { jsonPatchReducer } from "@open-smc/data/src/jsonPatchReducer";
import { from } from "rxjs";
import { serializeMiddleware } from "@open-smc/data/src/middleware/serializeMiddleware";
import { sampleApp } from "packages/samples-server/src/SampleApp";
import { patchRequestMiddleware } from "./middleware/patchRequestMiddleware";

export const dataStore =
    configureStore({
        reducer: jsonPatchReducer,
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