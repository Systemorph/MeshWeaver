import { PluginOption, ViteDevServer, WebSocket } from "vite";
import { UiHub } from "./UiHub";
import { getOrAdd } from "@open-smc/utils/src/getOrAdd";
import { Observable, Subscriber, TeardownLogic } from "rxjs";
import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";
import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";

export const methodName = "deliverMessage";

const apps = new Map<WebSocket.WebSocket, UiHub>();

export function playgroundServer() {
    return {
        name: "playgroundServer",
        configureServer(server: ViteDevServer) {
            const {ws} = server;

            ws.on('connection', (socket, data) => {
                const hub = new WebSocketHub(socket);

                const app = getOrAdd(apps, socket, socket => {
                    return new UiHub();
                });

                ws.on(methodName, (messageDelivery, client) => {
                    if (client.socket === socket) {
                        app.next(messageDelivery);
                    }
                });

                app.subscribe(messageDelivery => {
                    ws.clients.forEach(client => {
                        if (client.socket === socket) {
                            client.send(methodName, messageDelivery);
                        }
                    })
                });
            });
        },
    } as PluginOption
}

export class WebSocketHub extends Observable<MessageDelivery> implements MessageHub {
    constructor(private webSocket: WebSocket.WebSocket) {
        super(subscriber => {
            // const handler: WebSocketCustomListener<MessageDelivery> =
            //     (data, client) => subscriber.next(data, client);
            // ws.on(methodName, handler);
            // subscriber.add(() => ws.off(methodName, handler));
        });
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery){
        this.webSocket.send(methodName, value);
    }
}