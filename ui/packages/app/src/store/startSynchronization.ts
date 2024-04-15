import { distinctUntilChanged, map, Observer, Subscription } from "rxjs";
import { controls, entityStore, rootArea } from "./entityStore";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { syncControl } from "./syncControl";
import { effect } from "@open-smc/utils/src/operators/effect";
import { Transport } from "@open-smc/serialization/src/Transport";
import { sampleApp } from "@open-smc/backend/src/SampleApp";
import { selectByPath } from "@open-smc/data/src/operators/selectByPath";
import { Workspace } from "@open-smc/data/src/Workspace";
import { JsonPatchAction, jsonPatchActionCreator, jsonPatchReducer } from "@open-smc/data/src/jsonPatchReducer";
import { configureStore, Store } from "@reduxjs/toolkit";
import { serializeMiddleware } from "@open-smc/data/src/middleware/serializeMiddleware";
import { deserialize } from "@open-smc/serialization/src/deserialize";
import { toJsonPatch } from "@open-smc/data/src/operators/toJsonPatch";
import { RemoteStream } from "./RemoteStream";
import { serialize } from "@open-smc/serialization/src/serialize";

const backendHub = new Transport(sampleApp);

export const startSynchronization = () => {
    const subscription = new Subscription();

    const remoteStream = new RemoteStream(backendHub, new LayoutAreaReference("/"));

    subscription.add(
        remoteStream
            // .pipe(map(deserialize))
            .pipe(map(jsonPatchActionCreator))
            .subscribe(entityStore)
    );
    return () => subscription.unsubscribe();

    subscription.add(
        entityStore
            .pipe(toJsonPatch())
            // .pipe(map(serialize))
            .subscribe(remoteStream)
    )

    subscription.add(
        rootArea
            .pipe(distinctUntilChanged())
            .pipe(
                effect(
                    rootArea => {
                        const control$ =
                            controls.pipe(
                                map(selectByPath(`/${rootArea}`))
                            )

                        return syncControl(rootArea, control$);
                    }
                )
            )
            .subscribe()
    )

    // subscription.add(
    //     rootArea
    //         .pipe(distinctUntilChanged())
    //         .pipe(
    //             effect(
    //                 rootArea =>
    //                     syncControl(
    //                         null,
    //                         of(
    //                             new LayoutStackControl(
    //                                 [
    //                                     new EntityReference<UiControl>((UiControl as any).$type, rootArea)
    //                                 ]
    //                             )
    //                         )
    //                     )
    //             )
    //         )
    //         .subscribe(rootArea => {
    //             appStore.dispatch(setRoot(rootArea));
    //         })
    // );

    return () => subscription.unsubscribe();
}

class WorkspaceSync extends Subscription implements Observer<JsonPatchAction> {
    private store: Store;

    constructor(private workspace: Workspace) {
        super();

        this.store = configureStore({
            reducer: jsonPatchReducer,
            devTools: { name: 'jsonStore' },
            middleware: getDefaultMiddleware =>
                getDefaultMiddleware()
                    .prepend(serializeMiddleware)
        });

        this.workspace.subscribe(state => {
            // serialize, make diff, send patch request
        })
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: JsonPatchAction): void {
        this.store.dispatch(value);
        this.workspace.next(deserialize(value));
    }
}