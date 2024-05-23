import { IMessageHub } from "../MessageHub";

export function connectHubs(hub1: IMessageHub, hub2: IMessageHub) {
    const subscription = hub1.subscribe(hub2);
    subscription.add(hub2.subscribe(hub1));
    return subscription;
}