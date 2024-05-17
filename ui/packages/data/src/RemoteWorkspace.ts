import { filter, map, Subscription } from "rxjs";
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { DataChangedEvent } from "./contract/DataChangedEvent";
import { unpack } from "@open-smc/messaging/src/operators/unpack";
import { isEqual } from "lodash-es";
import { SubscribeRequest } from "./contract/SubscribeRequest";
import { WorkspaceAction } from "./workspaceReducer";
import FastJsonPatch from "fast-json-patch";
import { JsonPatch } from "./contract/JsonPatch";
import { PatchChangeRequest } from "./contract/PatchChangeRequest";
import { Workspace } from "./Workspace";
import { MessageHub } from '@open-smc/messaging/src/MessageHub';
import { deserialize } from "@open-smc/serialization/src/deserialize";
import { serialize } from "@open-smc/serialization/src/serialize";

export class RemoteWorkspace<T = unknown> extends Workspace<T> {
    subscription = new Subscription();
    json: unknown;

    constructor(
        private uiHub: MessageHub,
        private reference: WorkspaceReference,
        name?: string
    ) {
        super(undefined, name);

        this.subscription.add(
            uiHub
                .input
                .pipe(filter(messageOfType(DataChangedEvent)))
                .pipe(map(unpack))
                .pipe(
                    filter(
                    message =>
                        isEqual(message.reference, reference)
                        && !isEqual(message.changedBy, uiHub.address)
                    )
                )
                .pipe(map(dataChangedEventToJsonPatch))
                .subscribe(patch => {
                    this.json = FastJsonPatch.applyPatch(this.json, patch.operations, null, false).newDocument;
                    this.update(() => deserialize(this.json) as T);
                })
        );

        uiHub.sendRequest(new SubscribeRequest(reference))
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: WorkspaceAction): void {
        this.store.dispatch(value);
        const state = this.getState();
        const json = serialize(state);
        const operations = FastJsonPatch.compare(this.json, json);
        this.json = json;
        if (operations.length) {
            const patch = new JsonPatch(operations);
            this.uiHub.sendRequest(new PatchChangeRequest(null, this.reference, patch))
                .subscribe(response => {
                    if (response.status === "Failed") {
                        // TODO: handle rejection (4/16/2024, akravets)
                    }
                });
        }
    }

    post(message: unknown) {
        this.uiHub.post(message);
    }
}

const dataChangedEventToJsonPatch = (event: DataChangedEvent) => {
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