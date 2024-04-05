import { produceWithPatches } from "immer";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent";
import { JsonPatch } from "@open-smc/data/src/contract/JsonPatch";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { Observable, Observer, of, Subject } from "rxjs";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { toPatchOperation } from "./toPatchOperation";
import { EMPTY } from "rxjs";
import { handleRequest } from "@open-smc/messaging/src/handleRequest";
import { sendMessage } from "@open-smc/messaging/src/sendMessage";
import { basicStoreExample } from "@open-smc/layout/src/examples/basicStoreExample";

export class SampleApp extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    protected input = new Subject<MessageDelivery>();
    protected output = new Subject<MessageDelivery>();

    constructor() {
        super(subscriber => this.output.subscribe(subscriber));

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
                                state.instances.LineOfBusiness["1"].DisplayName = "Hello";
                            }
                        );

                    sendMessage(
                        this.output,
                        new DataChangedEvent(reference, new JsonPatch(patches.map(toPatchOperation)), "Patch")
                    )
                }, 1000);

                return of(new DataChangedEvent(reference, basicStoreExample, "Full"));

            }
        }

    patchChangeRequestHandler = () =>
        (message: PatchChangeRequest) => {
            console.log(message);
            return EMPTY as any;
        }
}

export const sampleApp = new SampleApp();