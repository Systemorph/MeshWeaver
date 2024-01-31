import { HttpTransportType, HubConnectionBuilder } from "@microsoft/signalr";
import { Subject } from "rxjs";

export function makeSignalrConnection() {
    const connection = new HubConnectionBuilder()
        .withUrl(`/signalR/application`, {
            //https://www.gresearch.co.uk/blog/article/signalr-on-kubernetes/
            skipNegotiation: true,
            transport: HttpTransportType.WebSockets
        })
        .withAutomaticReconnect({
            nextRetryDelayInMilliseconds: () => 5000
        })
        .build();

    const disconnectedSubject = new Subject<Error>();
    const reconnectedSubject = new Subject();

    connection.onreconnecting(disconnectedSubject.next);
    connection.onreconnected(reconnectedSubject.next);

    return {
        connection,
        onDisconnected: (subscriber: (error: Error) => void) => {
            const subscription = disconnectedSubject.subscribe(subscriber)
            return subscription.unsubscribe();
        },
        onReconnected: (subscriber: () => void) => {
            const subscription = reconnectedSubject.subscribe(subscriber)
            return subscription.unsubscribe();
        },
    }
}

export type SignalrConnection = ReturnType<typeof makeSignalrConnection>;