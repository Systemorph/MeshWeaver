import { filter, map, Subscription } from "rxjs";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { log } from "@open-smc/utils/src/operators/log";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { DataChangedEvent } from "./contract/DataChangedEvent";
import { unpack } from "@open-smc/messaging/src/operators/unpack";
import { isEqual } from "lodash-es";
import { jsonPatchActionCreator } from "./jsonPatchReducer";
import { sendRequest } from "@open-smc/messaging/src/sendRequest";
import { SubscribeRequest } from "./contract/SubscribeRequest";
import { WorkspaceAction } from "./workspaceReducer";
import jsonPatch from "fast-json-patch";
import { JsonPatch } from "./contract/JsonPatch";
import { PatchChangeRequest } from "./contract/PatchChangeRequest";
import { Workspace } from "./Workspace";

export class RemoteWorkspace<T = unknown> extends Workspace<T> {
    subscription = new Subscription();

    constructor(private transportHub: MessageHub, private reference: WorkspaceReference, name?: string) {
        super(undefined, name);

        this.subscription.add(
            transportHub
                .pipe(log('RemoteWorkspace input'))
                .pipe(filter(messageOfType(DataChangedEvent)))
                .pipe(map(unpack))
                .pipe(filter(message => isEqual(message.reference, reference)))
                .pipe(map(mapDataChangedEvent))
                .pipe(map(jsonPatchActionCreator))
                .subscribe(this.store.dispatch)
        );

        sendRequest(transportHub, new SubscribeRequest(reference));
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: WorkspaceAction): void {
        const oldState = this.getState();
        this.store.dispatch(value);
        const newState = this.getState();
        const operations = jsonPatch.compare(oldState, newState);
        if (operations.length) {
            const patch = new JsonPatch(operations);
            sendRequest(this.transportHub, new PatchChangeRequest(undefined, this.reference, patch))
                .subscribe(response => {
                    if (response.status === "Failed") {
                        // TODO: handle rejection (4/16/2024, akravets)
                    }
                });
        }
    }
}

const mapDataChangedEvent = (event: DataChangedEvent) => {
    const {change, changeType} = event;

    switch (changeType) {
        case "Full":
            return new JsonPatch([
                {op: "replace", path: "", value: change}
            ])
        case "Patch":
            return change as JsonPatch;
        default:
            console.warn(`Unknown change type ${changeType}`)
            break;
    }
}