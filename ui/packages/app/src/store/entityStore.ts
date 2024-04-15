import { configureStore } from "@reduxjs/toolkit";
import { jsonPatchReducer } from "@open-smc/data/src/jsonPatchReducer";
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
import { PathReferenceBase } from "@open-smc/data/src/contract/PathReferenceBase";
import { JsonPathReference } from "@open-smc/data/src/contract/JsonPathReference";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { WorkspaceProjection } from "@open-smc/data/src/WorkspaceProjection";
import { WorkspaceSlice } from "@open-smc/data/src/WorkspaceSlice";
import { sliceByPath } from "@open-smc/data/src/sliceByPath";
import { ObjectStore } from "@open-smc/data/src/ObjectStore";
import { selectByPath } from "@open-smc/data/src/operators/selectByPath";

// export const store =
//     configureStore<EntityStore>({
//         reducer: workspaceReducer,
//         devTools: {
//             name: "entityStore"
//         },
//         middleware: getDefaultMiddleware =>
//             getDefaultMiddleware({serializableCheck: false})
//                 .prepend(
//                     // patchRequestMiddleware(sampleApp),
//                     // serializeMiddleware
//                 ) as any,
//     });

// export const jsonStore = new ObjectStore<EntityStore>(undefined, "jsonStore");

export const entityStore = new Workspace<EntityStore>(undefined, "entityStore");

export const rootArea =
    entityStore.pipe(map(selectByPath<string>("/reference/area")));

export const collections =
    sliceByPath<EntityStore, Collection<Collection>>(entityStore, "/collections");

const uiControlType = (UiControl as any).$type;

export const controls =
    collections.pipe(map(selectByPath<Collection<UiControl>>(`/${uiControlType}`)));
