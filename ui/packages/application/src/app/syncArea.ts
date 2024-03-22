import { LayoutArea } from "../contract/LayoutArea";
import { Observable } from "rxjs";
import { Dispatch } from "@reduxjs/toolkit";
import { cleanupOldArea } from "./cleanupOldArea";
import { setNewArea } from "./setNewArea";
import { syncNestedAreas } from "./syncNestedAreas";

export const syncArea = (dispatch: Dispatch, data$: Observable<any>) =>
    (source: Observable<LayoutArea>) =>
        source
            .pipe(cleanupOldArea(dispatch))
            .pipe(syncNestedAreas(dispatch, data$))
            .pipe(setNewArea(dispatch, data$));