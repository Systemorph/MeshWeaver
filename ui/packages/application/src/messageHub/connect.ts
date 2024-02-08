import { MessageHub } from "./MessageHub";

export function connect(hub1: MessageHub, hub2: MessageHub) {
    const subscription = hub1.subscribe(hub2);
    subscription.add(hub2.subscribe(hub1));
    return subscription;
}