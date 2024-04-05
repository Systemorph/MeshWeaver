import { configureStore } from "@reduxjs/toolkit";
import { workspaceReducer } from "@open-smc/data/src/workspaceReducer";
import { distinctUntilChanged, from, map } from "rxjs";
import { deserialize } from "@open-smc/serialization/src/deserialize";
import { serializeMiddleware } from "@open-smc/data/src/middleware/serializeMiddleware";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { selectByReference } from "@open-smc/data/src/selectByReference";
import { CollectionReference } from "@open-smc/data/src/contract/CollectionReference";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";

export const entityStore =
    configureStore<EntityStore>({
        reducer: workspaceReducer,
        devTools: {
            name: "entityStore"
        },
        middleware: getDefaultMiddleware =>
            getDefaultMiddleware()
                .prepend(serializeMiddleware) as any,
    });

export const entityStore$ =
    from(entityStore)
        .pipe(map(deserialize));

export const rootArea$ =
    entityStore$
        .pipe(map(store => store.reference.area));

export const instances$ =
    entityStore$
        .pipe(map(store => store.instances));

export type Collection<T> = Record<string, T>;

const controlsCollectionReference =
    new CollectionReference((UiControl as any).$type);

export const controls$ =
    instances$
        .pipe(map(selectByReference<Collection<UiControl>>(controlsCollectionReference)));

export const rootControl$ =
    entityStore$
        .pipe(
            map(store => {
                    const controls
                        = selectByReference<Collection<UiControl>>(controlsCollectionReference)(store.instances);
                    return controls[store.reference.area];
                }
            )
        );