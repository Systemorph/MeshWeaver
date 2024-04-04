import { SignalrConnection } from "./makeSignalrConnection";
import { Observable } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";

const methodName = "DeliverMessage";

export class SignalrHub extends Observable<MessageDelivery> implements MessageHub {
    constructor(private connection: SignalrConnection) {
        super(subscriber => {
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
        void this.connection.connection.invoke(methodName, value);
    }
}

