import { Workspace } from "@open-smc/data/src/Workspace";
import { app$, appStore, ControlModel } from "./appStore";
import { distinctUntilChanged, map, Subscription } from "rxjs";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { setArea } from "./appReducer";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";

export const renderControlTo = (controlModelWorkspace: Workspace<ControlModel>, area: string) => {
    const subscription = new Subscription();

    subscription.add(
        controlModelWorkspace
            .pipe(distinctUntilEqual())
            .subscribe(control => {
                appStore.dispatch(setArea({
                    area,
                    control
                }))
            })
    );

    subscription.add(
        app$
            .pipe(map(appState => appState.areas[area]?.control))
            .pipe(distinctUntilChanged())
            .pipe(map(pathToUpdateAction("")))
            .subscribe(controlModelWorkspace)
    );

    return subscription;
}