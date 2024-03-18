import { Observable, Subject } from "rxjs";
import { MessageHub } from "./api/MessageHub";
import { MessageDelivery } from "./api/MessageDelivery";

export class SubjectHub extends Observable<MessageDelivery> implements MessageHub {
    protected input = new Subject<MessageDelivery>();
    protected output = new Subject<MessageDelivery>();

    constructor(init: (input: Subject<MessageDelivery>, output: Subject<MessageDelivery>) => void) {
        super(subscriber => this.output.subscribe(subscriber));

        init(this.input, this.output);
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: MessageDelivery) {
        this.input.next(value);
    }
}