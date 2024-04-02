import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilChanged, map, Observable, tap } from "rxjs";
import { LayoutArea } from "@open-smc/layout/src/contract/LayoutArea";
import { expandBindings } from "./expandBindings";
import { cloneDeepWith } from "lodash-es";
import { isEqual } from "lodash";
import { selectDeep } from "@open-smc/data/src/selectDeep";
import { setArea } from "./appReducer";

export const dataBinding = (
    data$: Observable<unknown>,
    parentDataContext: unknown,
    appDispatch: Dispatch
) =>
    (layoutArea: LayoutArea) => {
        if (layoutArea) {
            const {id, control, options, style} = layoutArea;

            if (control) {
                const componentTypeName = control.constructor.name;
                const {dataContext, ...props} = control;

                return data$
                    .pipe(map(selectDeep(dataContext)))
                    .pipe(distinctUntilChanged(isEqual))
                    .pipe(map(expandBindings(nestedAreasToIds(props), parentDataContext)))
                    .subscribe(
                        props => {
                            appDispatch(
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

            appDispatch(
                setArea({
                    id,
                    options,
                    style
                })
            );
        }
    }

const nestedAreasToIds = <T>(props: T): T =>
    cloneDeepWith(
        props,
        value => value instanceof LayoutArea
            ? value.id : undefined
    );