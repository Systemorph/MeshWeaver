import { filter, Observable } from "rxjs";
import { MessageDelivery } from "./api/MessageDelivery";

export function receiveMessage<T>(
    observable: Observable<MessageDelivery<T>>,
    handler: (message: T, envelope: MessageDelivery<T>) => void,
    predicate?: (envelope: MessageDelivery<T>) => boolean) {
    const subscription =
        (predicate ? observable.pipe(filter(predicate)) : observable)
            .subscribe(envelope => handler(envelope.message, envelope));
    return () => subscription.unsubscribe();
}