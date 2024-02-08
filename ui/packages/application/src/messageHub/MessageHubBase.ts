import { filter, first, firstValueFrom } from "rxjs";
import { ClassType } from "../contractMessage";
import { Hub } from "./Hub";
import { withTimeout } from "@open-smc/utils/src/promiseWithTimeout";
import { BaseEvent, ErrorEvent } from "@open-smc/application/src/application.contract";
import { MessageHub } from "@open-smc/application/src/messageHub/MessageHub";
import { MessageDelivery } from "./MessageDelivery";
import { filterByMessageType } from "../filterByMessageType";

export class MessageHubBase extends Hub<MessageDelivery> implements MessageHub {
    constructor() {
        super();

        ["sendMessage", "receiveMessage", "makeRequest"]
            .forEach((method) => {
                const th = this as any;
                th[method] = th[method].bind(th);
            });
    }

    ofType<TMessage>(messageType: ClassType<TMessage>) {
        return this.pipe(filterByMessageType(messageType));
    }

    sendMessage<TMessage>(message: TMessage, envelope?: Partial<MessageDelivery<TMessage>>) {
        this.next({
            ...(envelope ?? {}),
            message
        });
    }

    receiveMessage<TMessage>(
        messageType: ClassType<TMessage>,
        handler: (message: TMessage, envelope: MessageDelivery<TMessage>) => void,
        predicate?: (envelope: MessageDelivery<TMessage>) => boolean) {
        const subscription =
            this.pipe(filterByMessageType(messageType), filter(predicate ?? (() => true)))
                .subscribe((envelope) => handler(envelope.message, envelope));
        return () => subscription.unsubscribe();
    }

    async makeRequest<TResponse>(
        message: any,
        timeout?: number) {
        const requestId = getNewRequestId();

        const firstValue =
            firstValueFrom<MessageDelivery<TResponse>>(
                this.pipe(filter(({properties}) => properties.requestId === requestId))
                    .pipe(first())
            );

        this.sendMessage(message, {id: requestId});

        const result = firstValue.then(({message}) => message);

        return timeout ? withTimeout(result, timeout) : result;
    }

    subscribeToErrorEvent<TMessage extends BaseEvent>(sourceEvent: TMessage, handler: (message: string) => void) {
        return this.receiveMessage<ErrorEvent<TMessage>>(
            ErrorEvent,
            ({message}) => handler(message),
            ({message: {sourceEvent: {eventId}}}) => eventId === sourceEvent.eventId
        );
    }
}

let requestNumber = Date.now();

function getNewRequestId() {
    return (requestNumber++).toString();
}