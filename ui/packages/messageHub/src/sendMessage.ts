import { MessageDelivery } from "./api/MessageDelivery";
import { Observer } from "rxjs";

export function sendMessage<TMessage>(
    observer: Observer<MessageDelivery>,
    message: TMessage,
    envelope?: Partial<MessageDelivery<TMessage>>
) {
    observer.next({
        ...(envelope ?? {}),
        message
    });
}