import { MessageDelivery } from "../api/MessageDelivery";

export const messageOfType = <T>(ctor: abstract new(...args: any[]) => T) =>
    (envelope: MessageDelivery): envelope is MessageDelivery<T> =>
        envelope.message instanceof ctor;