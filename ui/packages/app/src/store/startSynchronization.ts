import { distinctUntilChanged, map, Observable, of, Subscription, switchMap } from "rxjs";
import { subscribeToBackend } from "@open-smc/data/src/subscribeToBackend";
import { controls, entityStore, jsonStore, rootArea } from "./entityStore";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { syncControl } from "./syncControl";
import { effect } from "@open-smc/utils/src/operators/effect";
import { Transport } from "@open-smc/serialization/src/Transport";
import { sampleApp } from "@open-smc/backend/src/SampleApp";
import { selectByPath } from "@open-smc/data/src/operators/selectByPath";

const backendHub = new Transport(sampleApp);

export const startSynchronization = () => {
    const subscription = new Subscription();

    subscription.add(subscribeToBackend(backendHub, new LayoutAreaReference("/"), jsonStore));

    // subscription.add(synchronizeWorkspace(jsonStore, entityStore));

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