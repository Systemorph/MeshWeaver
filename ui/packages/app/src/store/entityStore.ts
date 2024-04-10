import { configureStore } from "@reduxjs/toolkit";
import { workspaceReducer } from "@open-smc/data/src/workspaceReducer";
import { from, map } from "rxjs";
import { deserialize } from "@open-smc/serialization/src/deserialize";
import { serializeMiddleware } from "@open-smc/data/src/middleware/serializeMiddleware";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { selectByReference } from "@open-smc/data/src/operators/selectByReference";
import { CollectionReference } from "@open-smc/data/src/contract/CollectionReference";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { patchRequestMiddleware } from "./middleware/patchRequestMiddleware";
import { sampleApp } from "@open-smc/backend/src/SampleApp";
import { Workspace } from "@open-smc/data/src/Workspace";
import { WorkspaceReference } from "@open-smc/data/src/contract/WorkspaceReference";
import { JsonPathReference } from "@open-smc/data/src/contract/JsonPathReference";
import { Collection } from "@open-smc/data/src/contract/Collection";

// export const entityStore =
//     configureStore<EntityStore>({
//         reducer: workspaceReducer,
//         devTools: {
//             name: "entityStore"
//         },
//         middleware: getDefaultMiddleware =>
//             getDefaultMiddleware()
//                 .prepend(
//                     patchRequestMiddleware(sampleApp),
//                     serializeMiddleware
//                 ) as any,
//     });

export const entityStore =
    new Workspace<EntityStore>(undefined, "entityStore");

export const rootArea$ =
    entityStore
        .pipe(map(store => store?.reference?.area));

export const instances$ =
    entityStore
        .pipe(map(store => store.instances));

const uiControlType = (UiControl as any).$type;

const controlsCollectionReference =
    new CollectionReference<UiControl>(uiControlType);

export const controls$ =
    instances$
        .pipe(map(store => store.instances))
        .pipe(map(selectByReference(controlsCollectionReference)));

export const rootControl$ =
    entityStore
        .pipe(
            map(state => {
                    const controls = state.instances?.[uiControlType]
                    return controls[state.reference.area];
                }
            )
        );

const collectionsReference = 
    new JsonPathReference<Record<string, Collection>>("$.collections");

export const [collectionsWorkspace] =
    entityStore.map(collectionsReference);