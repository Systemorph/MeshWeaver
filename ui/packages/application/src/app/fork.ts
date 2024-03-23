import { Observable, share } from "rxjs";

export const fork = <T, P>(project: (source: Observable<T>) => Observable<P>) =>
    (source: Observable<T>) =>
        new Observable<T>(subscriber => {
            const sharedSource = source.pipe(share());

            const forkSubscription = project(sharedSource).subscribe();

            const subscription = sharedSource.subscribe({
                next: value => subscriber.next(value),
                complete: () => subscriber.complete(),
                error: err => subscriber.error(err)
            });

            return () => {
                subscription.unsubscribe();
                forkSubscription.unsubscribe();
            }
        });