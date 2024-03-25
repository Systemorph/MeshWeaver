import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilChanged, map, Observable, Subscription } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { Control } from "../contract/controls/Control";
import { isOfType } from "../contract/ofType";
import { LayoutStackControl } from "../contract/controls/LayoutStackControl";
import { keys } from "lodash";
import { ignoreNestedAreas } from "./ignoreNestedAreas";
import { removeArea, RootState } from "./store";
import { layoutAreaToUi } from "./layoutAreaToUi";

export const syncLayoutArea = (
    data$: Observable<unknown>,
    uiDispatch: Dispatch,
    ui$: Observable<RootState>,
    dataDispatch: Dispatch,
    parentContext?: unknown
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
                                    area$.pipe(syncLayoutArea(data$, uiDispatch, ui$, dataDispatch))
                                        .subscribe();
                            });
                    }
                )
            );

            subscription.add(
                source
                    .pipe(distinctUntilChanged(ignoreNestedAreas))
                    // .pipe(uiToData(ui$, dataDispatch))
                    .pipe(layoutAreaToUi(uiDispatch, data$, parentContext))
                    .subscribe()
            );

            subscription.add(
                nestedAreas$.subscribe(
                    layoutAreas => {
                        keys(state).forEach(id => {
                            if (!layoutAreas?.find(area => area.id === id)) {
                                uiDispatch(removeArea(id));
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

const getNestedAreas = (control: Control) => {
    if (isOfType(control, LayoutStackControl)) {
        return control?.areas;
    }
}