import { map, Observable, Observer } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { IMessageHub } from "@open-smc/messaging/src/MessageHub";
import { deserialize } from "@open-smc/serialization/src/deserialize";
import { serialize } from "@open-smc/serialization/src/serialize";

export class SerializationMiddleware extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    constructor(private hub: IMessageHub) {
        super(
            subscriber =>
                hub.pipe(map(deserialize))
                    .subscribe(value => {
                        setTimeout(() => subscriber.next(value))
                    })
        );
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery) {
        setTimeout(() => this.hub.next(serialize(value)));
    }
}