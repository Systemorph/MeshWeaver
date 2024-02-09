import { Observable } from "rxjs";
import { methodName } from "./playgroundServer";
import { WebSocketClient, WebSocketCustomListener, WebSocketServer } from "vite";
import { MessageDelivery } from "@open-smc/message-hub/src/MessageDelivery";
import { MessageHub } from "@open-smc/message-hub/src/MessageHub";

let appId = 1;

const apps = new Map<WebSocketClient, any>();

export class HmrServerHub extends Observable<MessageDelivery> implements MessageHub {
    constructor(private ws: WebSocketServer) {
        super(subscriber => {
            // const handler: WebSocketCustomListener<MessageDelivery> =
            //     (data, client) => subscriber.next(data, client);
            // ws.on(methodName, handler);
            // subscriber.add(() => ws.off(methodName, handler));
        });

        // ws.on(methodName, (data, client) => {
        //     const app = getOrAdd(apps, client, client => {
        //
        //     });
        //
        //     console.log('Message from client:', data.msg) // Hey!
        //     // reply only to the client (if needed)
        //     client.send('my:ack', {msg: 'Hi! I got your message!'})
        // })

        ws.on('connection', (clientSocket, data) => {
            ws.clients.forEach(client => {
                if (client.socket === clientSocket) {

                    client.send('my:greetings', {msg: appId++})
                }
            })
        });
    }

    complete(): void {
    }

    error(): void {
    }

    next(value: MessageDelivery) {
        this.ws.send(methodName, value);
    }
}