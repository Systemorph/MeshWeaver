import { MessageDelivery } from "../api/MessageDelivery";

export function messageOfType<T>(ctor: abstract new(...args: any[]) => T) {
    return (envelope: MessageDelivery): envelope is MessageDelivery<T> =>
        envelope.message instanceof ctor
}