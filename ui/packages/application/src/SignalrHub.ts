import { SignalrConnection } from "./makeSignalrConnection";
import { Observable } from "rxjs";
import { MessageHub } from "./messageHub/MessageHub";
import { MessageDelivery } from "./MessageDelivery";

export class SignalrHub extends Observable<MessageDelivery> implements MessageHub {
    constructor(private connection: SignalrConnection) {
        super(subscriber => {
            const methodName = "HandleEvent";
            const handler = (args: any) => subscriber.next(args);
            connection.connection.on(methodName, handler);
            subscriber.add(() => connection.connection.off(methodName, handler));
        });
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery) {
        void this.connection.connection.invoke("DeliverMessage", value);
    }
}

