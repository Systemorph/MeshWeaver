import { from, map, Observable, skip, switchMap } from "rxjs";
import { LayoutAreaModel, RootState } from "./store";
import { Dispatch } from "@reduxjs/toolkit";
import { LayoutArea } from "../contract/LayoutArea";
import { keys } from "lodash";
import { isOfType } from "../contract/ofType";
import { Binding } from "../dataBinding/resolveBinding";
import { PropsInput } from "./expandBindings";
import { DataInput, JsonPatch } from "@open-smc/data/src/data.contract";
import { jsonPatch } from "@open-smc/data/src/workspace";

export const uiToData = (ui$: Observable<RootState>, dataDispatch: Dispatch) =>
    (source: Observable<LayoutArea>) =>
        new Observable<LayoutArea>(subscriber => {
            const subscription =
                source
                    .pipe(
                        switchMap(
                            layoutArea => {
                                const {control} = layoutArea;
                                const {$type, dataContext, ...props} = control;

                                return ui$
                                    .pipe(map(ui => ui.areas[layoutArea.id]))
                                    .pipe(layoutAreaModelToData(props, dataContext, dataDispatch));
                            }
                        )
                    )
                    .subscribe();

            return () => subscription.unsubscribe();
        });

const keyChanged = <T, TKey extends keyof T>(keys: TKey[]) =>
    (previous: T, current: T) => keys.map(key => previous)

const layoutAreaModelToData = <TProps, TContext>(
    props: PropsInput<TProps>,
    dataContext: DataInput<TContext>,
    dataDispatch: Dispatch
) =>
    (source: Observable<LayoutAreaModel>) =>
        new Observable(subscriber => {
            const dataContextWorkspace = makeDataContextWorkspace(dataContext);
            const dataContext$ = from(dataContextWorkspace)

            dataContext$.subscribe(
                () => dataDispatch(
                    jsonPatch(
                        new JsonPatch([
                            {
                                op: "replace",
                                path: bindingPath,
                                value
                            }
                        ])
                    )
                )
            )
            const boundKeys = keys(props)
                .filter(key => isOfType((props as any)[key], Binding));

            const subscription =
                source
                    .subscribe(
                        layoutAreaModel => {

                        }
                    );
            return () => subscription.unsubscribe();
        });