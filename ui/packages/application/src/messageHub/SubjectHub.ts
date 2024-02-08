import { Observable, Subject } from "rxjs";
import { MessageHub } from "./MessageHub";
import { MessageDelivery } from "./MessageDelivery";

export class SubjectHub extends Observable<MessageDelivery> implements MessageHub {
    constructor(
        protected input = new Subject<MessageDelivery>,
        protected output = new Subject<MessageDelivery>
    ) {
        super(subscriber => output.subscribe(subscriber));
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery): void {
        this.input.next(value);
    }
}