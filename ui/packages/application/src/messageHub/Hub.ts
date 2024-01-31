import { Observable, Observer, Subject, from } from "rxjs";

export class Hub<T = unknown> extends Observable<T> implements Observer<T> {
    readonly inputSubj: Subject<T>;
    readonly outputSubj: Subject<T>;

    constructor() {
        const input = new Subject<T>();
        const output = new Subject<T>();

        super(subscriber => output.subscribe(subscriber));

        this.inputSubj = input;
        this.outputSubj = output;
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: T) {
        this.inputSubj.next(value);
    }

    exposeAs<THub extends Hub<T>>(hub: THub) {
        const subscription = this.inputSubj.subscribe(hub.outputSubj);
        subscription.add(hub.inputSubj.subscribe(this.outputSubj));
        return subscription;
    }
}