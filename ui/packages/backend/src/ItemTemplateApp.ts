import { enablePatches, produceWithPatches } from "immer";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent";
import { JsonPatch } from "@open-smc/data/src/contract/JsonPatch";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { map, Observable, Observer, of, Subject } from "rxjs";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { toPatchOperation } from "./toPatchOperation";
import { handleRequest } from "@open-smc/messaging/src/handleRequest";
import { sendMessage } from "@open-smc/messaging/src/sendMessage";
import { basicStoreExample } from "@open-smc/layout/src/examples/basicStoreExample";
import { log } from "@open-smc/utils/src/operators/log";
import { serialize } from "@open-smc/serialization/src/serialize";
import { deserialize } from "@open-smc/serialization/src/deserialize";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse";
import { TransportEmulation } from "./TransportEmulation";
import { itemTemplateExample } from "@open-smc/layout/src/examples/itemTemplateExample";

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

export const itemTemplateApp = new TransportEmulation(new ItemTemplateApp());