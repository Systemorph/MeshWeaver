import { filter, map, Observable, Observer, ReplaySubject, Subject, take } from "rxjs";
import { MessageDelivery } from "./api/MessageDelivery";
import { v4 } from "uuid";
import { messageOfType } from "./operators/messageOfType";
import { unpack } from "./operators/unpack";
import { Request } from "./api/Request";

export type IMessageHub = Observable<MessageDelivery> & Observer<MessageDelivery>;

export class MessageHub extends Observable<MessageDelivery> implements IMessageHub {
    readonly input = new Subject<MessageDelivery>();
    readonly output = new Subject<MessageDelivery>();

    constructor(public readonly address?: unknown) {
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
            .pipe(filter(({ properties }) => properties?.requestId === id))
            .pipe(take(1))
            .pipe(map(unpack))
            .subscribe(result);

        this.post(message, { id });

        return result.pipe(take(1));
    }
}
