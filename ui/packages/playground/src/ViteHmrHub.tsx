import { Observable } from "rxjs";
import { MessageHub } from "@open-smc/application/src/messageHub/MessageHub.ts";
import { MessageDelivery } from "@open-smc/application/src/SignalrHub.ts";

const hmr = import.meta.hot!;

const methodName = "deliverMessage";

export class ViteHmrHub extends Observable<MessageDelivery> implements MessageHub {
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