import { Observable, TeardownLogic } from "rxjs";

// TODO: see finalize (3/22/2024, akravets)
export function withTeardownBefore<T>(teardown: TeardownLogic) {
    return (source: Observable<T>) => {
        return new Observable<T>(subscriber => {
            subscriber.add(teardown);
            subscriber.add(source.subscribe(subscriber));
        });
    };
}