import { Observable, switchMap, TeardownLogic } from "rxjs";
import { withTeardownBefore } from "./withTeardownBefore";

export const withCleanup = <T>(project: (value: T) => TeardownLogic) =>
    (source: Observable<T>) =>
        source.pipe(
            switchMap(
                value =>
                    new Observable(subscriber => subscriber.next(value))
                        .pipe(withTeardownBefore(project(value)))
            )
        );