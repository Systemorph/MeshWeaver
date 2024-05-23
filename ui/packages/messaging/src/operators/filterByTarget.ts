import { filter } from "rxjs";
import { MessageDelivery } from "../api/MessageDelivery";

export function filterByTarget(target: any) {
    return filter<MessageDelivery>(envelope => envelope.target === target);
}