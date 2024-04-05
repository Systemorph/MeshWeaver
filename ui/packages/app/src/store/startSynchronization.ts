import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { distinctUntilChanged, Subscription } from "rxjs";
import { subscribeToDataChanges } from "@open-smc/data/src/subscribeToDataChanges";
import { removeArea, setRoot } from "./appReducer";
import { appStore } from "./appStore";
import { entityStore, rootArea$ } from "./entityStore";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { syncControl } from "./syncControl";
import { effect } from "@open-smc/utils/src/operators/effect";
import { withPreviousValue } from "@open-smc/utils/src/operators/withPreviousValue";

export const startSynchronization = (hub: MessageHub) => {
    const subscription = new Subscription();

    subscription.add(subscribeToDataChanges(hub, new LayoutAreaReference("/"), entityStore.dispatch));

    subscription.add(
        rootArea$
            .pipe(distinctUntilChanged())
            .pipe(effect(syncControl))
            .pipe(withPreviousValue())
            .subscribe(([previous, current]) => {
                if (previous) {
                    appStore.dispatch(removeArea(previous));
                }
                appStore.dispatch(setRoot(current));
            })
    );

    // subscription.add(
    //     entityStore$
    //         .pipe(syncLayoutArea(data$, appStore.dispatch, app$, dataStore.dispatch))
    //         .pipe(distinctUntilKeyChanged("id"))
    //         .pipe(withPreviousValue())
    //         .subscribe(([previous, current]) => {
    //             if (previous?.id) {
    //                 appStore.dispatch(removeArea(previous.id));
    //             }
    //             appStore.dispatch(setRoot(current?.id ? current.id : null));
    //         })
    // );

    return () => subscription.unsubscribe();
}
