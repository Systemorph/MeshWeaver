import { Observable, Subject } from "rxjs";
import { MessageHub } from "./api/MessageHub";
import { MessageDelivery } from "./api/MessageDelivery";

export class SubjectHub extends Observable<MessageDelivery> implements MessageHub {
    constructor(
        protected input = new Subject<MessageDelivery>,
        protected output = new Subject<MessageDelivery>
    ) {
        super(subscriber => output.subscribe(subscriber));
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: MessageDelivery) {
        this.input.next(value);
    }
}