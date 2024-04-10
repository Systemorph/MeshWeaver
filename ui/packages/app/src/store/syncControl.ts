import { combineLatest, distinctUntilChanged, map, merge, Observable, skip, Subscription, switchMap } from "rxjs";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { collections, controls } from "./entityStore";
import { app$, appStore } from "./appStore";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { isEqual, keys, pickBy, toPairs, omit } from "lodash-es";
import { removeArea, setArea } from "./appReducer";
import { effect } from "@open-smc/utils/src/operators/effect";
import { nestedAreasToIds } from "./dataBinding";
import { expandBindings } from "./expandBindings";
import { Binding, isBinding } from "@open-smc/layout/src/contract/Binding";
import { bindingToPatchAction } from "./bindingToPatchAction";
import { sliceByReference } from "@open-smc/data/src/sliceByReference";

export const syncControl = (
    areaId: string,
    control$: Observable<UiControl>,
    parentDataContext?: unknown
) => {
    const state: Record<string, Subscription> = {};

    const subscription = new Subscription();

    const nestedAreas$ =
        control$
            .pipe(map(nestedAreas))
            .pipe(distinctUntilChanged<EntityReference<UiControl>[]>(isEqual));

    subscription.add(
        nestedAreas$
            .subscribe(
                references => {
                    references?.filter(reference => !state[reference.id])
                        .forEach(reference => {
                            state[reference.id] = syncControl(
                                reference.id,
                                controls.pipe(
                                    map(controls => controls?.[reference.id])
                                )
                            );
                        });
                }
            )
    );

    const dataContext$ =
        control$
            .pipe(map(control => control?.dataContext))
            .pipe(distinctUntilChanged(isEqual));

    const props$ =
        control$
            .pipe(map(control => control && omit(control, 'dataContext')))
            .pipe(distinctUntilChanged(isEqual));

    subscription.add(
        dataContext$
            .pipe(
                effect(
                    dataContext => {
                        const subscription = new Subscription();

                        const dataContextWorkspace =
                            sliceByReference(collections, dataContext, areaId);

                        const setArea$ =
                            combineLatest([dataContextWorkspace, control$.pipe(distinctUntilChanged<UiControl>(isEqual))])
                                .pipe(
                                    map(
                                        ([dataContextState, control]) => {
                                            if (control) {
                                                const componentTypeName = control.constructor.name;
                                                const {dataContext, ...props} = control;
                                                const boundProps =
                                                    expandBindings(nestedAreasToIds(props), parentDataContext)(dataContextState);

                                                return setArea({
                                                    id: areaId,
                                                    control: {
                                                        componentTypeName,
                                                        props: boundProps
                                                    }
                                                });
                                            }
                                            else {
                                                return setArea({
                                                    id: areaId
                                                })
                                            }
                                        }
                                    )
                                );

                        const dataContextPatch$ =
                            props$
                                .pipe(
                                    switchMap(
                                        props => {
                                            const bindings: Record<string, Binding> = pickBy(props, isBinding);

                                            return merge(
                                                ...toPairs(bindings)
                                                    .map(
                                                        ([key, binding]) =>
                                                            app$
                                                                .pipe(map(appState => appState.areas[areaId].control.props[key]))
                                                                .pipe(distinctUntilChanged(isEqual))
                                                                .pipe(skip(1))
                                                                .pipe(map(bindingToPatchAction(binding)))
                                                    )
                                            );
                                        })
                                );

                        subscription.add(dataContextWorkspace.subscription);
                        subscription.add(setArea$.subscribe(appStore.dispatch));
                        subscription.add(dataContextPatch$.subscribe(dataContextWorkspace));

                        return subscription;
                    }
                )
            )
            .subscribe()
    )

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