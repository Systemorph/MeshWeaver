import { Observable } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { methodName } from "@open-smc/samples-server/src/contract";

const hmr = import.meta.hot!;

export class HmrClientHub extends Observable<MessageDelivery> implements MessageHub {
    constructor() {
        super(subscriber => {
            const handler = (args: any) => subscriber.next(args);
            hmr.on(methodName, handler);
            subscriber.add(() => hmr.off(methodName, handler));
        });
    }

    complete(): void {
    }

    error(): void {
    }

    next(value: MessageDelivery) {
        hmr.send(methodName, value);
    }
}