import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { sendMessage } from "@open-smc/messaging/src/sendMessage";
import { PatchAction, patchActionCreator } from "./jsonPatchReducer";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { filter, map, Observer } from "rxjs";
import { SubscribeRequest } from "./contract/SubscribeRequest";
import { UnsubscribeDataRequest } from "./contract/UnsubscribeDataRequest";
import { DataChangedEvent } from "./contract/DataChangedEvent";
import { JsonPatch } from "./contract/JsonPatch";
import { sendRequest } from "@open-smc/messaging/src/sendRequest";
import { isEqual } from "lodash-es";
import { unpack } from "@open-smc/messaging/src/operators/unpack";
import { log } from "@open-smc/utils/src/operators/log";
import { WorkspaceReferenceBase } from "./contract/WorkspaceReferenceBase";

export function subscribeToBackend(
    hub: MessageHub,
    reference: WorkspaceReferenceBase,
    observer: Observer<PatchAction>
) {
    const subscription =
        hub
            .pipe(log("client input"))
            .pipe(filter(messageOfType(DataChangedEvent)))
            .pipe(map(unpack))
            .pipe(filter(message => isEqual(message.reference, reference)))
            .subscribe(({change, changeType}) => {
                if (changeType === "Full") {
                    observer.next(
                        patchActionCreator(
                            new JsonPatch([
                                {op: "replace", path: "", value: change}
                            ])
                        )
                    );
                } else if (changeType === "Patch") {
                    observer.next(patchActionCreator(change as JsonPatch))
                } else {
                    console.warn(`Unknown change type ${changeType}`)
                }
            });

    sendRequest(hub, new SubscribeRequest(reference));

    subscription
        .add(() => sendMessage(hub, new UnsubscribeDataRequest(reference)));

    return subscription;
}