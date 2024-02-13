import { Observable } from "rxjs";
import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";
import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";
import { methodName } from "../../playground-server/src/contract.ts";

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