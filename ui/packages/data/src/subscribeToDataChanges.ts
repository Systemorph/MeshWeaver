import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { sendMessage } from "@open-smc/messaging/src/sendMessage";
import { setState, patch } from "./workspaceReducer";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { filter, map, Observer } from "rxjs";
import { SubscribeRequest } from "./contract/SubscribeRequest";
import { UnsubscribeDataRequest } from "./contract/UnsubscribeDataRequest";
import { DataChangedEvent } from "./contract/DataChangedEvent";
import { JsonPatch } from "./contract/JsonPatch";
import { sendRequest } from "@open-smc/messaging/src/sendRequest";
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { isEqual } from "lodash-es";
import { unpack } from "@open-smc/messaging/src/operators/unpack";
import { Action } from "redux";
import { log } from "@open-smc/utils/src/operators/log";

export function subscribeToDataChanges(
    hub: MessageHub,
    reference: WorkspaceReference,
    observer: Observer<Action>
) {
    const subscription =
        hub
            .pipe(log("client input"))
            .pipe(filter(messageOfType(DataChangedEvent)))
            .pipe(map(unpack))
            .pipe(filter(message => isEqual(message.reference, reference)))
            .subscribe(({change, changeType}) => {
                if (changeType === "Full") {
                    observer.next(setState(change));
                }
                else if (changeType === "Patch") {
                    observer.next(patch(change as JsonPatch))
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