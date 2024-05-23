import { MessageDelivery } from "../api/MessageDelivery";

export const unpack = <T>({message}: MessageDelivery<T>) => message;