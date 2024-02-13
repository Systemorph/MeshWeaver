import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";
import { filter } from "rxjs";

export const ofContractType = <T>(ctor: new(...args: any[]) => T) =>
    filter((envelope: MessageDelivery): envelope is MessageDelivery<T> =>
        (envelope.message as any).$type === (ctor as any).$type);