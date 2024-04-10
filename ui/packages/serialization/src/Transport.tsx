import { map, Observable, Observer } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { deserialize } from "./deserialize";
import { serialize } from "./serialize";

export class Transport extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    constructor(private hub: MessageHub) {
        super(
            subscriber =>
                hub.pipe(map(deserialize)).subscribe(subscriber)
        );
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery) {
        this.hub.next(serialize(value));
    }
}