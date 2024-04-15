import { filter, map, Observable, Observer } from "rxjs";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { WorkspaceReference } from "packages/data/src/contract/WorkspaceReference";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { unpack } from "@open-smc/messaging/src/operators/unpack";
import { isEqual } from "lodash-es";
import { sendRequest } from "@open-smc/messaging/src/sendRequest";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest";
import { JsonPatch } from "@open-smc/data/src/contract/JsonPatch";
import { log } from "@open-smc/utils/src/operators/log";

export class RemoteStream extends Observable<JsonPatch> implements Observer<JsonPatch> {
    constructor(private transportHub: MessageHub, private reference: WorkspaceReference) {
        super(subscriber => {
            transportHub
                .pipe(log('RemoteStream input'))
                .pipe(filter(messageOfType(DataChangedEvent)))
                .pipe(map(unpack))
                .pipe(filter(message => isEqual(message.reference, reference)))
                .pipe(map(mapDataChangedEvent))
                .subscribe(subscriber)
            // TODO: instead of doing this, RemoteStream should keep state and multicast it
            // to all subscribers, in this case deserialization of patch value should be avoided (4/15/2024, akravets)
            sendRequest(transportHub, new SubscribeRequest(reference));
        });
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: JsonPatch): void {
        sendRequest(this.transportHub, new PatchChangeRequest(null, this.reference, value))
            .subscribe(response => {
                if (response.status === "Failed") {
                    // TODO: on fail, it should issue the full state as a patch (4/15/2024, akravets)
                }
            });
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