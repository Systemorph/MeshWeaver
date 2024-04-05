import { Dispatch } from "@reduxjs/toolkit";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { sendMessage } from "@open-smc/messaging/src/sendMessage";
import { initState, patch } from "./workspaceReducer";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { filter, map } from "rxjs";
import { SubscribeRequest } from "./contract/SubscribeRequest";
import { UnsubscribeDataRequest } from "./contract/UnsubscribeDataRequest";
import { DataChangedEvent } from "./contract/DataChangedEvent";
import { JsonPatch } from "./contract/JsonPatch";
import { sendRequest } from "@open-smc/messaging/src/sendRequest";
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { isEqual } from "lodash-es";
import { unpack } from "@open-smc/messaging/src/operators/unpack";

export function subscribeToDataChanges(
    hub: MessageHub,
    reference: WorkspaceReference,
    dispatch: Dispatch
) {
    const subscription =
        hub
            .pipe(filter(messageOfType(DataChangedEvent)))
            .pipe(map(unpack))
            .pipe(filter(message => isEqual(message.reference, reference)))
            .subscribe(({change, changeType}) => {
                if (changeType === "Full") {
                    dispatch(initState(change));
                }
                else if (changeType === "Patch") {
                    dispatch(patch(change as JsonPatch))
                }
                else {
                    console.warn(`Unknown change type ${changeType}`)
                }
            });

    sendRequest(hub, new SubscribeRequest(reference));

    subscription
        .add(() => sendMessage(hub, new UnsubscribeDataRequest(reference)));

    return subscription;
}