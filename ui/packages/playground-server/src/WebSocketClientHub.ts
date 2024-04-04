import { Observable, Subscription } from "rxjs";
import type { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import type { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { WebSocketClient, WebSocketCustomListener, WebSocketServer } from "vite";
import { methodName } from "./contract";

// do not serialize subscriptions due to circular references
(Subscription.prototype as any).toJSON = (key: string) => {
    return {}
}

export class WebSocketClientHub extends Observable<MessageDelivery> implements MessageHub {
    constructor(private webSocketClient: WebSocketClient, webSocket: WebSocketServer) {
        super(subscriber => {
            const handler: WebSocketCustomListener<MessageDelivery> =
                (data, client) => client === webSocketClient && subscriber.next(data);
            webSocket.on(methodName, handler);
            subscriber.add(() => webSocket.off(methodName, handler));
        });
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery) {
        this.webSocketClient.send(methodName, value);
    }
}