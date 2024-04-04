import { MessageDelivery } from "./MessageDelivery";

export type MessageHandler<TObservable, T> = (this: TObservable, envelope: MessageDelivery<T>) => void;