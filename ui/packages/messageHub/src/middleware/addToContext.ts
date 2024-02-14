import { MessageHub } from "../api/MessageHub";
import { filterByTarget } from "../operators/filterByTarget";
import { addSender } from "../operators/addSender";
import { ofType } from "../operators/ofType";

export function addToContext(context: MessageHub, hub: MessageHub, address: any) {
    const subscription = context.pipe(filterByTarget(address)).subscribe(hub);

    subscription.add(hub.pipe(addSender(address)).subscribe(context));

    subscription.add(hub.pipe(ofType(AddToContext))
        .subscribe(({message: {hub, address}}) => addToContext(context, hub, address)));

    return subscription;
}

export class AddToContext {
    constructor(public hub: MessageHub, public address: any) {
    }
}