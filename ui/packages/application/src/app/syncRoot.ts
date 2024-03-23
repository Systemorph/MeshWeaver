import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilKeyChanged, Observable, tap } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { setRoot } from "./store";

export const syncRoot = (dispatch: Dispatch) =>
    (source: Observable<LayoutArea>) =>
        source
            .pipe(distinctUntilKeyChanged("id"))
            .pipe(
                tap(
                    layoutArea =>
                        dispatch(setRoot(layoutArea ? layoutArea.id : null))
                )
            )
