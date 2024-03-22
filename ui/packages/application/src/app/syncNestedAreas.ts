import { Dispatch } from "@reduxjs/toolkit";
import { merge, Observable, of, OperatorFunction, skipWhile, switchMap, takeWhile } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { fork } from "./fork";
import { nestedAreas } from "./nestedAreas";
import { syncArea } from "./syncArea";

export const syncNestedAreas = (dispatch: Dispatch, data$: Observable<any>): OperatorFunction<LayoutArea, LayoutArea> =>
    (source: Observable<LayoutArea>) =>
        source.pipe(
            fork(
                source =>
                    source
                        .pipe(nestedAreas())
                        .pipe(
                            switchMap(
                                nestedAreas =>
                                    nestedAreas
                                        ? merge(
                                            nestedAreas.map(
                                                area$ =>
                                                    area$
                                                        .pipe(takeWhile(Boolean))
                                                        .pipe(syncArea(dispatch, data$))
                                            )
                                        )
                                        : of()
                            )
                        )
            )
        );