import { Workspace } from "@open-smc/data/src/Workspace";
import { map, Subscription } from "rxjs";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";

export function syncWorkspaces<T1 = unknown, T2 = unknown>(workspace1: Workspace<T1>, workspace2: Workspace<T2>) {
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