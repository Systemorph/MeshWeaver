import { enablePatches, produceWithPatches } from "immer";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest.ts";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent.ts";
import { JsonPatch } from "@open-smc/data/src/contract/JsonPatch.ts";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference.ts";
import { map, Observable, Observer, of, Subject } from "rxjs";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest.ts";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery.ts";
import { handleRequest } from "@open-smc/messaging/src/handleRequest.ts";
import { sendMessage } from "@open-smc/messaging/src/sendMessage.ts";
import { log } from "@open-smc/utils/src/operators/log.ts";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse.ts";
import { toPatchOperation } from "../toPatchOperation.ts";
import { basicStoreExample } from "./basicStoreExample.ts";

enablePatches();

export class SampleApp extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    protected input = new Subject<MessageDelivery>();
    protected output = new Subject<MessageDelivery>();

    constructor() {
        super(
            subscriber =>
                this.output
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
        this.input.next(value);
    }

    subscribeRequestHandler = () =>
        (message: SubscribeRequest) => {
            const {reference} = message;

            if (reference instanceof LayoutAreaReference) {
                setTimeout(() => {
                    const [nextState, patches] =
                        produceWithPatches(
                            basicStoreExample,
                            state => {
                                state.collections.LineOfBusiness["1"].DisplayName = "Hello";
                            }
                        );

                    sendMessage(
                        this.output,
                        new DataChangedEvent(reference, new JsonPatch(patches.map(toPatchOperation)), "Patch")
                    )
                }, 1000);

                return of(new DataChangedEvent(reference, basicStoreExample, "Full"));
            }

            throw 'Reference type not supported';
        }

    patchChangeRequestHandler = () =>
        (message: PatchChangeRequest) => {
            console.log(message);
            return of(new DataChangeResponse("Committed"));
        }
}