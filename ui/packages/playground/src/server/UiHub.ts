import { Observable, Subject } from "rxjs";
import { WebSocketClient } from "vite";
import { MessageDelivery } from "@open-smc/message-hub/src/MessageDelivery";
import { MessageHub } from "@open-smc/message-hub/src/MessageHub";

export class UiHub extends Observable<MessageDelivery> implements MessageHub {
    private output = new Subject<MessageDelivery>();

    constructor(client: WebSocketClient) {
        super(subscriber => {
            subscriber.add(this.output.subscribe(subscriber));
        });
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery) {
        // TODO: process incoming messages (2/5/2024, akravets)
        console.log("Received message", value);
    }
}