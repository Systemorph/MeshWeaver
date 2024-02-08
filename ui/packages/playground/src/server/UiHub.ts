import { Observable, Subject } from "rxjs";
import { MessageHub } from "@open-smc/application/src/messageHub/MessageHub";
import { WebSocketClient } from "vite";

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