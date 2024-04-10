import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { distinctUntilChanged, map, Observable, of, Subscription } from "rxjs";
import { subscribeToDataChanges } from "@open-smc/data/src/subscribeToDataChanges";
import { removeArea, setRoot } from "./appReducer";
import { appStore } from "./appStore";
import { entityStore, rootArea } from "./entityStore";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { syncControl } from "./syncControl";
import { effect } from "@open-smc/utils/src/operators/effect";
import { withPreviousValue } from "@open-smc/utils/src/operators/withPreviousValue";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";

export const startSynchronization = (hub: MessageHub) => {
    const subscription = new Subscription();

    subscription.add(subscribeToDataChanges(hub, new LayoutAreaReference("/"), entityStore));

    subscription.add(
        rootArea
            .pipe(distinctUntilChanged())
            .pipe(
                effect(
                    rootArea =>
                        syncControl(
                            null,
                            of(
                                new LayoutStackControl(
                                    [
                                        new EntityReference<UiControl>((UiControl as any).$type, rootArea)
                                    ]
                                )
                            )
                        )
                )
            )
            .subscribe(rootArea => {
                appStore.dispatch(setRoot(rootArea));
            })
    );

    return () => subscription.unsubscribe();
}