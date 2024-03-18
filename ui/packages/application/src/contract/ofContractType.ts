import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";
import { filter } from "rxjs";

export const isOfContractType = <T>(obj: any, ctor: new(...args: any[]) => T): obj is T =>
    obj?.$type === (ctor as any).$type;

export const ofContractType = <T>(ctor: new(...args: any[]) => T) =>
    filter(
        (envelope: MessageDelivery): envelope is MessageDelivery<T> =>
            isOfContractType(envelope.message, ctor)
    );