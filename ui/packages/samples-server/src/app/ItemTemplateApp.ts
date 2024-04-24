import { enablePatches, produceWithPatches } from "immer";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest.ts";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent.ts";
import { JsonPatch } from "@open-smc/data/src/contract/JsonPatch.ts";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference.ts";
import { filter, map, Observable, Observer, of, Subject } from "rxjs";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest.ts";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery.ts";
import { toPatchOperation } from "src/toPatchOperation.ts";
import { handleRequest } from "@open-smc/messaging/src/handleRequest.ts";
import { sendMessage } from "@open-smc/messaging/src/sendMessage.ts";
import { log } from "@open-smc/utils/src/operators/log.ts";
import { serialize } from "@open-smc/serialization/src/serialize.ts";
import { deserialize } from "@open-smc/serialization/src/deserialize.ts";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse.ts";
import { SerializationMiddleware } from "../../../middleware/src/SerializationMiddleware.ts";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType.ts";
import { itemTemplateExample } from "./itemTemplateExample.ts";

enablePatches();

export class ItemTemplateApp extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    protected input = new Subject<MessageDelivery>();
    protected output = new Subject<MessageDelivery>();

    constructor() {
        super(
            subscriber =>
                this.output
                    .pipe(map(serialize))
                    .subscribe(subscriber)
        );

        this.input
            .pipe(handleRequest(SubscribeRequest, this.subscribeRequestHandler()))
            .subscribe(this.output);

        this.input
            .pipe(handleRequest(PatchChangeRequest, this.patchChangeRequestHandler()))
            .subscribe(this.output);

        this.input
            .pipe(filter(messageOfType(PatchChangeRequest)))
            .pipe(log("patch request"))
            .subscribe();
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: MessageDelivery) {
        this.input.next(deserialize(value));
    }

    subscribeRequestHandler = () =>
        (message: SubscribeRequest) => {
            const {reference} = message;

            if (reference instanceof LayoutAreaReference) {
                return of(new DataChangedEvent(reference, itemTemplateExample, "Full"));
            }
        }

    patchChangeRequestHandler = () =>
        (message: PatchChangeRequest) => {
            return of(new DataChangeResponse("Committed"));
        }
}

export const itemTemplateApp = new SerializationMiddleware(new ItemTemplateApp());