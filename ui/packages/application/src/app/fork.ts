import { Observable } from "rxjs";

export const fork = <T, P>(project: (source: Observable<T>) => Observable<P>) =>
    (source: Observable<T>) =>
        new Observable<T>(subscriber => {
            const subscription = source.subscribe({
                next: value => subscriber.next(value),
                complete: () => subscriber.complete(),
                error: err => subscriber.error(err)
            });

            const forkSubscription = project(source).subscribe();

            return () => {
                forkSubscription.unsubscribe();
                subscription.unsubscribe();
            }
        });