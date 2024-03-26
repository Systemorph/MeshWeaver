import { Dispatch } from "@reduxjs/toolkit";
import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";
import { v4 } from "uuid";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";
import {
    DataChangedEvent,
    JsonPatch,
    SubscribeDataRequest,
    UnsubscribeDataRequest
} from "./data.contract";
import { initState, jsonPatch } from "./workspace";
import { messageOfType } from "@open-smc/message-hub/src/operators/messageOfType";
import { filter } from "rxjs";

export function subscribeToDataChanges(hub: MessageHub, workspaceReference: any, dispatch: Dispatch) {
    const id = v4();

    const subscription = hub.pipe(messageOfType(DataChangedEvent))
        .pipe(filter(({message}) => message.id === id))
        .subscribe(({message: {id, change}}) => {
            if (change instanceof JsonPatch) {
                dispatch(jsonPatch(change))
            }
            else {
                dispatch(initState(change));
            }
        });

    sendMessage(hub, new SubscribeDataRequest(id, workspaceReference));

    subscription
        .add(() => sendMessage(hub, new UnsubscribeDataRequest([id])));

    return subscription;
}