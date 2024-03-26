import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilChanged, map, Observable, switchMap, tap } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { setArea } from "./store";
import { expandBindings, PropsInput } from "./expandBindings";
import { cloneDeepWith } from "lodash-es";
import { isEqual } from "lodash";
import { selectAll } from "@open-smc/data/src/selectAll";

export const layoutAreaToUi = (dispatch: Dispatch, data$: Observable<unknown>, parentDataContext: unknown) =>
    (source: Observable<LayoutArea>) =>
        new Observable<LayoutArea>(subscriber => {
            const subscription =
                source
                    .pipe(
                        switchMap(
                            layoutArea => {
                                const {id, control, options, style} = layoutArea;
                                const {dataContext, ...props} = control;

                                const componentTypeName = control.constructor.name;

                                return data$
                                    .pipe(map(selectAll(dataContext)))
                                    .pipe(distinctUntilChanged(isEqual))
                                    // .pipe(tap(() => console.log('--------------', componentTypeName)))
                                    // .pipe(tap(console.log))
                                    .pipe(map(expandBindings(nestedAreasToIds(props as PropsInput), parentDataContext)))
                                    // .pipe(tap(console.log))
                                    .pipe(
                                        tap(
                                            props => {
                                                dispatch(
                                                    setArea({
                                                        id,
                                                        control: {
                                                            componentTypeName,
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

const nestedAreasToIds = <T>(props: T): T =>
    cloneDeepWith(
        props,
        value => value instanceof LayoutArea
            ? value.id : undefined
    );