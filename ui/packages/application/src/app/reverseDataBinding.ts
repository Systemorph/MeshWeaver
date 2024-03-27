import { from, map, Observable } from "rxjs";
import { LayoutAreaModel, RootState } from "./store";
import { Dispatch } from "@reduxjs/toolkit";
import { LayoutArea } from "../contract/LayoutArea";
import { keys } from "lodash";
import { PropsInput } from "./expandBindings";
import { DataInput, JsonPatch } from "@open-smc/data/src/data.contract";
import { jsonPatch } from "@open-smc/data/src/workspace";
import { Binding } from "../contract/Binding";

export const reverseDataBinding = ($ui: Observable<RootState>, dataDispatch: Dispatch) =>
    (layoutArea: LayoutArea) => {
        const {control} = layoutArea;
        const {dataContext, ...props} = control;

        const boundKeys = keys(props)
            .filter(key => (props as any)[key] instanceof Binding);

        const propObservables = boundKeys.map($ui)

        return ui$
            .pipe(map(ui => ui.areas[layoutArea.id]))
            .pipe(layoutAreaModelToData(props, dataContext, dataDispatch));
    }

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