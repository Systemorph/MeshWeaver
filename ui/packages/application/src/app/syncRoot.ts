import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilKeyChanged, Observable, tap } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { setRoot } from "./store";
import { fork } from "./fork";

export const syncRoot = (dispatch: Dispatch) =>
    (source: Observable<LayoutArea>) =>
        source
            .pipe(
                fork(
                    stream =>
                        stream
                            .pipe(distinctUntilKeyChanged("id"))
                            .pipe(
                                tap(
                                    layoutArea =>
                                        dispatch(setRoot(layoutArea ? layoutArea.id : null))
                                )
                            )
                )
            );