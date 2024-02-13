import { map } from "rxjs";
import { MessageDelivery } from "../api/MessageDelivery";

export function unpack<T>() {
    return map<MessageDelivery<T>, T>(({message}) => message);
}