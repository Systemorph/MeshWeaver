import { filter } from "rxjs";
import { MessageDelivery } from "../api/MessageDelivery";
import { isOfType } from "@open-smc/application/src/contract/ofType";

export function messageOfType<T>(ctor: new(...args: any[]) => T) {
    return filter<MessageDelivery>((envelope): envelope is MessageDelivery<T> =>
        isOfType(envelope.message, ctor));
}