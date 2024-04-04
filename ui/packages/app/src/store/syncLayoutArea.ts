import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilChanged, map, Observable, Subscription } from "rxjs";
import { LayoutArea } from "@open-smc/layout/src/contract/LayoutArea";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { keys } from "lodash-es";
import { ignoreNestedAreas } from "@open-smc/layout/src/operators/ignoreNestedAreas";
import { AppState } from "./appStore";
import { dataBinding } from "./dataBinding";
import { reverseDataBinding } from "./reverseDataBinding";
import { effect } from "@open-smc/utils/src/operators/effect";
import { removeArea } from "./appReducer";

export const syncLayoutArea = (
    data$: Observable<unknown>,
    appDispatch: Dispatch,
    app$: Observable<AppState>,
    dataDispatch: Dispatch,
    parentDataContext?: unknown
) =>
    (source: Observable<LayoutArea>) =>
        new Observable<LayoutArea>(subscriber => {
            const state: Record<string, Subscription> = {};
            const nestedAreas$ = source.pipe(subAreas());

            const subscription = new Subscription();

            // TODO: pass dataContext to nested areas (3/25/2024, akravets)
            subscription.add(
                nestedAreas$.subscribe(
                    layoutAreas => {
                        layoutAreas?.filter(layoutArea => !state[layoutArea.id])
                            .forEach(layoutArea => {
                                const area$ =
                                    nestedAreas$.pipe(
                                        map(
                                            layoutAreas =>
                                                layoutAreas.find(area => area.id === layoutArea.id)
                                        )
                                    );

                                state[layoutArea.id] =
                                    area$.pipe(syncLayoutArea(data$, appDispatch, app$, dataDispatch))
                                        .subscribe();
                            });
                    }
                )
            );

            subscription.add(
                source
                    .pipe(distinctUntilChanged(ignoreNestedAreas))
                    .pipe(effect(dataBinding(data$, parentDataContext, appDispatch)))
                    .pipe(effect(reverseDataBinding(app$, data$, dataDispatch)))
                    .subscribe()
            );

            subscription.add(
                nestedAreas$.subscribe(
                    layoutAreas => {
                        keys(state).forEach(id => {
                            if (!layoutAreas?.find(area => area.id === id)) {
                                appDispatch(removeArea(id));
                                state[id].unsubscribe();
                                delete state[id];
                            }
                        })
                    }
                )
            );

            subscription.add(
                source.subscribe(
                    layoutArea =>
                        subscriber.next(layoutArea)
                )
            );

            return () => subscription.unsubscribe();
        });

export const subAreas = () =>
    (source: Observable<LayoutArea>) =>
        source
            .pipe(
                map(
                    layoutArea => layoutArea?.control &&
                        getNestedAreas(layoutArea?.control)
                )
            );

const getNestedAreas = (control: UiControl) => {
    if (control instanceof LayoutStackControl) {
        return control?.areas;
    }
}