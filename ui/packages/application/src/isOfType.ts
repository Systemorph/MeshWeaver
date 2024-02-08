import { TypeGuard } from "@open-smc/utils/src/TypeGuard";
import { MessageDelivery } from "@open-smc/application/src/messageHub/MessageDelivery";

export function isOfType<T, TResult extends TypeGuard<MessageDelivery, MessageDelivery<T>>>(ctor: new(...args: any[]) => T): TResult {
    const ret = (envelope: MessageDelivery) => (envelope.message as any).$type === (ctor as any).$type;
    return ret as TResult;
}