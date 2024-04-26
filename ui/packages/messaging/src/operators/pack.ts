import { MessageDelivery } from "../api/MessageDelivery";

export const pack = (envelope: Partial<MessageDelivery> = {}) =>
    <T>(message: T): MessageDelivery<T> => ({...envelope, message})