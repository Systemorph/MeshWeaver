import { filter, map, Observable, ReplaySubject, Subject, take } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { UiAddress } from "./contract/UiAddress";
import { v4 } from "uuid";
import { Request } from "@open-smc/messaging/src/api/Request";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { unpack } from "@open-smc/messaging/src/operators/unpack";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";

export class UiHub extends Observable<MessageDelivery> implements MessageHub {
    readonly input = new Subject<MessageDelivery>();
    readonly output = new Subject<MessageDelivery>();

    readonly address = new UiAddress(v4())

    constructor() {
        super(subscriber => this.output.subscribe(subscriber));
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: MessageDelivery): void {
        this.input.next(value);
    }

    post(message: unknown, envelope?: Partial<MessageDelivery>) {
        const sender = this.address;

        this.output.next({
            ...(envelope ?? {}),
            sender,
            message
        });
    }

    sendRequest<T>(message: Request<T>) {
        const id = v4();

        const result = new ReplaySubject<T>();

        this.input
            .pipe(filter(messageOfType(message.responseType)))
            .pipe(filter(({properties}) => properties?.requestId === id))
            .pipe(take(1))
            .pipe(map(unpack))
            .subscribe(result);

        this.post(message, {id});

        return result.pipe(take(1));
    }
}