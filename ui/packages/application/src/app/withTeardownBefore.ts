import { Observable, TeardownLogic } from "rxjs";

export function withTeardownBefore<T>(teardown: TeardownLogic) {
    return (source: Observable<T>) => {
        return new Observable<T>(subscriber => {
            subscriber.add(teardown);
            subscriber.add(source.subscribe(subscriber));
        });
    };
}