import { SignalrConnection } from "./makeSignalrConnection";
import { Observable, Observer } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { serialize } from "@open-smc/serialization/src/serialize";
import { deserialize } from "@open-smc/serialization/src/deserialize";

const methodName = "DeliverMessage";

export class SignalrHub extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    constructor(private connection: SignalrConnection) {
        super(subscriber => {
            const handler = (message: any) => subscriber.next(deserialize(message));
            connection.connection.on(methodName, handler);
            subscriber.add(() => connection.connection.off(methodName, handler));
        });
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery) {
        void this.connection.connection.invoke(methodName, serialize(value));
    }
}

