import { map } from "rxjs";
import { MessageDelivery } from "../api/MessageDelivery";

export function addSender(sender: any) {
    return map<MessageDelivery, MessageDelivery>(envelope => {
        return {
            ...envelope,
            sender
        }
    });
}