import { Workspace } from "@open-smc/data/src/Workspace";
import { map, Subscription } from "rxjs";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";

export function syncWorkspaces(workspace1: Workspace, workspace2: Workspace) {
    const subscription = new Subscription();

    subscription.add(
        workspace1
            .pipe(distinctUntilEqual())
            .pipe(map(pathToUpdateAction("")))
            .subscribe(workspace2)
    );

    subscription.add(
        workspace2
            .pipe(distinctUntilEqual())
            .pipe(map(pathToUpdateAction("")))
            .subscribe(workspace1)
    );

    return subscription;
}