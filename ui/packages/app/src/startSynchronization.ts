import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";
import { distinctUntilKeyChanged, Subscription } from "rxjs";
import { subscribeToDataChanges } from "@open-smc/data/src/subscribeToDataChanges";
import { EntireWorkspace, LayoutAreaReference } from "@open-smc/data/src/data.contract";
import { syncLayoutArea } from "./store/syncLayoutArea";
import { withPreviousValue } from "@open-smc/utils/src/operators/withPreviousValue";
import { removeArea, setRoot } from "./store/appReducer";
import { app$, appStore } from "./store/appStore";
import { data$, dataStore } from "@open-smc/data/src/dataStore";
import { layout$, layoutStore } from "@open-smc/layout/src/layoutStore";

export const startSynchronization = (backendHub: MessageHub) => {
    const subscription = new Subscription();

    subscription.add(subscribeToDataChanges(backendHub, new EntireWorkspace(), dataStore.dispatch));

    subscription.add(subscribeToDataChanges(backendHub, new LayoutAreaReference("/"), layoutStore.dispatch));

    subscription.add(
        layout$
            .pipe(syncLayoutArea(data$, appStore.dispatch, app$, dataStore.dispatch))
            .pipe(distinctUntilKeyChanged("id"))
            .pipe(withPreviousValue())
            .subscribe(([previous, current]) => {
                if (previous?.id) {
                    appStore.dispatch(removeArea(previous.id));
                }
                appStore.dispatch(setRoot(current?.id ? current.id : null));
            })
    );

    return () => subscription.unsubscribe();
}