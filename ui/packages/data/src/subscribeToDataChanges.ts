import { Dispatch } from "@reduxjs/toolkit";
import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";
import { v4 } from "uuid";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";
import {
    DataChangedEvent,
    JsonPatch,
    PatchOperation,
    SubscribeDataRequest,
    UnsubscribeDataRequest
} from "./data.contract";
import { initState, jsonPatches } from "./workspace";
import { Patch } from "immer";
import { messageOfType } from "@open-smc/message-hub/src/operators/messageOfType";
import { isOfType } from "@open-smc/application/src/contract/ofType";
import { filter, map } from "rxjs";

export function subscribeToDataChanges(hub: MessageHub, workspaceReference: any, dispatch: Dispatch) {
    const id = v4();

    const subscription = hub.pipe(messageOfType(DataChangedEvent))
        .pipe(filter(({message}) => message.id === id))
        .subscribe(({message: {id, change}}) => {
            if (isOfType(change, JsonPatch)) {
                dispatch(jsonPatches(change.operations?.map(toImmerPatch)))
            } else {
                dispatch(initState(change));
            }
        });

    sendMessage(hub, new SubscribeDataRequest(id, workspaceReference));

    subscription
        .add(() => sendMessage(hub, new UnsubscribeDataRequest([id])));

    return subscription;
}

function toImmerPatch(patch: PatchOperation): Patch {
    const {op, path, value} = patch;

    return {
        op,
        path: path?.split("/"),
        value
    }
}