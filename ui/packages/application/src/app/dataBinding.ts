import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilChanged, map, Observable } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { setArea } from "./store";
import { expandBindings } from "./expandBindings";
import { cloneDeepWith } from "lodash-es";
import { isEqual } from "lodash";
import { selectAll } from "@open-smc/data/src/selectAll";

export const dataBinding = (data$: Observable<unknown>, parentDataContext: unknown, uiDispatch: Dispatch) =>
    (layoutArea: LayoutArea) => {
        const {id, control, options, style} = layoutArea;

        if (control) {
            const componentTypeName = control.constructor.name;
            const {dataContext, ...props} = control;

            return data$
                .pipe(map(selectAll(dataContext)))
                .pipe(distinctUntilChanged(isEqual))
                .pipe(map(expandBindings(nestedAreasToIds(props), parentDataContext)))
                .subscribe(
                    props => {
                        uiDispatch(
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
                );
        }

        uiDispatch(
            setArea({
                id,
                options,
                style
            })
        );
    }

const nestedAreasToIds = <T>(props: T): T =>
    cloneDeepWith(
        props,
        value => value instanceof LayoutArea
            ? value.id : undefined
    );