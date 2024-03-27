import { finalize, Observable, switchMap, TeardownLogic, Unsubscribable } from "rxjs";
import { isFunction } from "lodash-es";

export const effect = <T>(effect: (value: T) => TeardownLogic) =>
    (source: Observable<T>) =>
        source.pipe(
            switchMap(
                value => {
                    const teardown = effect(value);
                    return new Observable<T>(subscriber => source.subscribe(subscriber))
                        .pipe(finalize(() => teardown && execFinalizer(teardown)));
                }
            )
        );

// copied from rxjs/src/internal/Subscription.ts
function execFinalizer(finalizer: Unsubscribable | (() => void)) {
    if (isFunction(finalizer)) {
        finalizer();
    } else {
        finalizer.unsubscribe();
    }
}