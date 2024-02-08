import { filter, Observable } from "rxjs";
import { MessageDelivery } from "./MessageDelivery";

export function receiveMessage<
    TPredicate extends (envelope: MessageDelivery) => boolean,
    THandler = TPredicate extends (envelope: MessageDelivery) => envelope is MessageDelivery<infer T>
        ? (envelope: MessageDelivery<T>) => void : (envelope: MessageDelivery) => void
>(
    observable: Observable<MessageDelivery>,
    handler: THandler,
    predicate: TPredicate) {
    const subscription =
        (predicate ? observable.pipe(filter(predicate)) : observable)
            .subscribe(handler);
    return () => subscription.unsubscribe();
}