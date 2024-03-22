import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilKeyChanged, Observable } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { fork } from "./fork";
import { withCleanup } from "./withCleanup";
import { removeArea } from "./store";

export const cleanupOldArea = (dispatch: Dispatch) =>
    (source: Observable<LayoutArea>) =>
        source.pipe(
            fork(
                source =>
                    source.pipe(distinctUntilKeyChanged('id'))
                        .pipe(
                            withCleanup(
                                layoutArea => () => layoutArea && dispatch(removeArea(layoutArea.id))
                            )
                        )
            )
        );