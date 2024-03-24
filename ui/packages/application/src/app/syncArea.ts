import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilChanged, map, Observable, Subscription } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { Control } from "../contract/controls/Control";
import { isOfType } from "../contract/ofType";
import { LayoutStackControl } from "../contract/controls/LayoutStackControl";
import { toLayoutAreaModel } from "./setNewArea";
import { keys } from "lodash";
import { ignoreNestedAreas } from "./ignoreNestedAreas";
import { removeArea, setArea } from "./store";

export const syncArea = (dispatch: Dispatch, data$: Observable<any>) =>
    (source: Observable<LayoutArea>) =>
        new Observable<LayoutArea>(subscriber => {
            const state: Record<string, Subscription> = {};
            const nestedAreas$ = source.pipe(subAreas());

            nestedAreas$
                .subscribe(layoutAreas => {
                    layoutAreas?.filter(layoutArea => !state[layoutArea.id])
                        .forEach(layoutArea => {
                            const area$ =
                                nestedAreas$.pipe(
                                    map(
                                        layoutAreas =>
                                            layoutAreas.find(area => area.id === layoutArea.id)
                                    )
                                );
                            state[layoutArea.id] = area$.pipe(syncArea(dispatch, data$)).subscribe();
                        });
                });

            source
                .pipe(distinctUntilChanged(ignoreNestedAreas))
                .subscribe(layoutArea =>
                    layoutArea && dispatch(setArea(toLayoutAreaModel(layoutArea))));

            nestedAreas$
                .subscribe(layoutAreas => {
                    keys(state).forEach(id => {
                        if (!layoutAreas?.find(area => area.id === id)) {
                            dispatch(removeArea(id));
                            state[id].unsubscribe();
                            delete state[id];
                        }
                    })
                });

            source.subscribe(layoutArea => subscriber.next(layoutArea));
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