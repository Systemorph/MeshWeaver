import { Dispatch } from "@reduxjs/toolkit";
import { Observable, switchMap, tap } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { setArea } from "./store";
import { expandBindings } from "./expandBindings";
import { isOfType } from "../contract/ofType";
import { cloneDeepWith } from "lodash-es";
import { expandAllReferences } from "@open-smc/data/src/expandAllReferences";

export const layoutAreaToUi = (dispatch: Dispatch, data$: Observable<unknown>, parentDataContext: unknown) =>
    (source: Observable<LayoutArea>) =>
        new Observable<LayoutArea>(subscriber => {
            const subscription =
                source
                    .pipe(
                        switchMap(
                            layoutArea => {
                                const {id, control, options, style} = layoutArea;
                                const {$type, dataContext, ...props} = control;

                                return data$
                                    .pipe(expandAllReferences(dataContext))
                                    .pipe(expandBindings(nestedAreasToIds(props), parentDataContext))
                                    .pipe(
                                        tap(
                                            props => {
                                                dispatch(
                                                    setArea({
                                                        id,
                                                        control: {
                                                            componentTypeName: $type.split(".").pop(),
                                                            props
                                                        },
                                                        options,
                                                        style
                                                    })
                                                );
                                            }
                                        )
                                    );
                            }
                        )
                    )
                    .subscribe();

            subscription.add(
                source.subscribe(
                    layoutArea => subscriber.next(layoutArea)
                )
            );

            return () => subscription.unsubscribe();
        });

const nestedAreasToIds = (props: {}) =>
    cloneDeepWith(
        props,
        value => isOfType(value, LayoutArea) ? value.id : undefined
    );