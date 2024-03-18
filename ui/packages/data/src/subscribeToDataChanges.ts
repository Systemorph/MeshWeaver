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
import { isOfContractType, ofContractType } from "@open-smc/application/src/contract/ofContractType";
import { initState, jsonPatches } from "./workspace";
import { Patch } from "immer";

export function subscribeToDataChanges(hub: MessageHub, workspaceReference: any, dispatch: Dispatch) {
    const id = v4();

    sendMessage(hub, new SubscribeDataRequest(id, workspaceReference));

    const subscription = hub.pipe(ofContractType(DataChangedEvent))
        .subscribe(({message}) => {
            if (isOfContractType(message.change, JsonPatch)) {
                dispatch(jsonPatches(message.change.operations?.map(toImmerPatch)))
            } else {
                dispatch(initState(message.change));
            }
        });

    return subscription
        .add(() => sendMessage(hub, new UnsubscribeDataRequest([id])));
}

function toImmerPatch(patch: PatchOperation): Patch {
    const {op, path, value} = patch;

    return {
        op,
        path: path?.split("/"),
        value
    }
}