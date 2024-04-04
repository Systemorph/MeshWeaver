import { MessageHub } from "../api/MessageHub";
import { filterByTarget } from "../operators/filterByTarget";
import { addSender } from "../operators/addSender";
import { messageOfType } from "../operators/messageOfType";
import { sendMessage } from "../sendMessage";
import { AddToContextRequest } from "../api/AddToContextRequest";
import { AddedToContext } from "../api/AddedToContext";

export function addToContext(context: MessageHub, hub: MessageHub, address: any) {
    const subscription = context.pipe(filterByTarget(address)).subscribe(hub);

    subscription.add(hub.pipe(addSender(address)).subscribe(context));

    subscription.add(hub.pipe(messageOfType(AddToContextRequest))
        .subscribe(({message: {hub, address}}) => addToContext(context, hub, address)));

    sendMessage(hub, new AddedToContext(context));

    return subscription;
}