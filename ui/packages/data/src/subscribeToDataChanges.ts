import { Dispatch } from "@reduxjs/toolkit";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { v4 } from "uuid";
import { sendMessage } from "@open-smc/messaging/src/sendMessage";
import { initState, patch } from "./workspaceReducer";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { filter } from "rxjs";
import { SubscribeRequest } from "./contract/SubscribeRequest";
import { UnsubscribeDataRequest } from "./contract/UnsubscribeDataRequest";
import { DataChangedEvent } from "./contract/DataChangedEvent";
import { JsonPatch } from "./contract/JsonPatch";

export function subscribeToDataChanges(hub: MessageHub, workspaceReference: any, dispatch: Dispatch) {
    const id = v4();

    const subscription = hub.pipe(messageOfType(DataChangedEvent))
        .pipe(filter(({message}) => message.id === id))
        .subscribe(({message: {id, change}}) => {
            if (change instanceof JsonPatch) {
                dispatch(patch(change))
            }
            else {
                dispatch(initState(change));
            }
        });

    sendMessage(hub, new SubscribeRequest(id, workspaceReference));

    subscription
        .add(() => sendMessage(hub, new UnsubscribeDataRequest([id])));

    return subscription;
}