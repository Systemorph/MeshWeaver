import { configureStore } from "@reduxjs/toolkit";
import { workspaceReducer } from "@open-smc/data/src/workspaceReducer";
import { LayoutArea } from "./contract/LayoutArea";
import { from, map } from "rxjs";
import { deserialize } from "@open-smc/serialization/src/deserialize";
import { serializeMiddleware } from "@open-smc/data/src/serializeMiddleware";

export const layoutStore =
    configureStore<LayoutArea>({
        reducer: workspaceReducer,
        devTools: {
            name: "layout"
        },
        middleware: getDefaultMiddleware =>
            getDefaultMiddleware()
                .concat(serializeMiddleware) as any,
    });

export const layout$ =
    from(layoutStore)
        .pipe(map(deserialize));