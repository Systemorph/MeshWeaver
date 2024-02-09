import { Observable } from "rxjs";
import type { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";
import type { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";
import { WebSocketClient, WebSocketCustomListener, WebSocketServer } from "vite";
import { methodName } from "./playgroundServer";

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