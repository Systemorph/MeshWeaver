import { distinctUntilChanged, map, Observable, Subscription } from "rxjs";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { controls$, instances$ } from "./entityStore";
import { app$, appStore } from "./appStore";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { selectByReference } from "@open-smc/data/src/selectByReference";
import { isEqual, keys } from "lodash-es";
import { removeArea } from "./appReducer";
import { effect } from "@open-smc/utils/src/operators/effect";
import { dataBinding } from "./dataBinding";
import { reverseDataBinding } from "./reverseDataBinding";

export const syncControl = (
    areaId: string,
    parentDataContext?: unknown
) => {
    const state: Record<string, Subscription> = {};
    const subscription = new Subscription();

    const control$ = controls$
        .pipe(map(controls => controls[areaId]));

    const nestedAreas$ =
        control$
            .pipe(map(nestedAreas))
            .pipe(distinctUntilChanged<EntityReference[]>(isEqual));

    subscription.add(
        nestedAreas$
            .subscribe(
                references => {
                    references?.filter(reference => !state[reference.id])
                        .forEach(reference => {
                            state[reference.id] = syncControl(
                                reference.id,
                                controls$
                                    .pipe(map(selectByReference(reference)))
                            );
                        });
                }
            )
    );

    subscription.add(
        control$
            .pipe(distinctUntilChanged<UiControl>(isEqual))
            .pipe(effect(dataBinding(areaId, parentDataContext)))
            // .pipe(effect(reverseDataBinding(app$, data$, entityStoreDispatch)))
            .subscribe()
    );

    subscription.add(
        nestedAreas$
            .subscribe(
                references => {
                    keys(state).forEach(id => {
                        if (!references?.find(area => area.id === id)) {
                            appStore.dispatch(removeArea(id));
                            state[id].unsubscribe();
                            delete state[id];
                        }
                    })
                }
            )
    );

    return subscription;
}

const nestedAreas = (control: UiControl) => {
    if (control instanceof LayoutStackControl) {
        return control?.areas;
    }
}