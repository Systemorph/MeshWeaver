import { Observable, TeardownLogic, Unsubscribable } from "rxjs";
import { isFunction } from "lodash";

export const effect = <T>(effect: (value: T) => TeardownLogic) =>
    (source: Observable<T>) =>
        new Observable<T>(subscriber => {
            let teardown: TeardownLogic;
            const execTeardown = () => teardown && execFinalizer(teardown);

            source.subscribe({
                next: value => {
                    execTeardown();
                    teardown = effect(value);
                    subscriber.next(value);
                },
                error: err => subscriber.error(err),
                complete: () => subscriber.complete()
            });

            return execTeardown;
        });

// copied from rxjs/src/internal/Subscription.ts
function execFinalizer(finalizer: Unsubscribable | (() => void)) {
    if (isFunction(finalizer)) {
        finalizer();
    } else {
        finalizer.unsubscribe();
    }
}