import {
    filter,
    map,
    of,
    Subscription,
    take
} from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest.ts";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference.ts";
import { isEqual } from "lodash-es";
import { handleRequest } from "@open-smc/messaging/src/handleRequest.ts";
import { UnsubscribeDataRequest } from "@open-smc/data/src/contract/UnsubscribeDataRequest.ts";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent.ts";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType.ts";
import { SamplesApp } from "./SamplesApp";
import { MessageHub } from "@open-smc/messaging/src/MessageHub";
import { serialize } from "@open-smc/serialization/src/serialize";
import { toJsonPatch } from "@open-smc/data/src/operators/toJsonPatch";
import { pack } from "@open-smc/messaging/src/operators/pack";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { jsonPatchActionCreator } from "@open-smc/data/src/jsonPatchReducer";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse";
import { log } from "@open-smc/utils/src/operators/log";

export class SamplesServer {
    subscription = new Subscription();

    constructor(private serverHub: MessageHub) {
        this.subscription.add(
            serverHub.input
                .pipe(handleRequest(SubscribeRequest, this.subscribeRequestHandler()))
                .subscribe(serverHub.output)
        );
    }

    subscribeRequestHandler = () =>
        ({ message, sender }: MessageDelivery<SubscribeRequest>) => {
            const { reference } = message;

            const subscription = new Subscription();

            const samplesApp = new SamplesApp(this.serverHub, reference as LayoutAreaReference);

            subscription.add(
                samplesApp.subscription
            );

            subscription.add(
                this.serverHub
                    .input
                    .pipe(
                        filter(messageOfType(UnsubscribeDataRequest)),
                        filter(({ message }) => isEqual(message.reference, reference))
                    )
                    .subscribe(request => {
                        subscription.unsubscribe();
                    })
            );

            let lastMessage: MessageDelivery;

            subscription.add(
                this.serverHub
                    .input
                    .pipe(
                        filter(messageOfType(PatchChangeRequest)),
                        filter(({ message }) => isEqual(message.reference, reference)),
                        handleRequest(PatchChangeRequest,
                            (delivery: MessageDelivery<PatchChangeRequest>) => {
                                lastMessage = delivery;
                                const { message, sender } = delivery;
                                samplesApp.next(jsonPatchActionCreator(message.change));
                                return of(new DataChangeResponse("Committed"));
                            }
                        )
                    )
                    .subscribe(this.serverHub.output)
            );

            subscription.add(
                samplesApp
                    .pipe(
                        map(serialize),
                        toJsonPatch()
                    )
                    .pipe(
                        map(
                            patch =>
                                new DataChangedEvent(
                                    reference,
                                    patch,
                                    "Patch",
                                    lastMessage instanceof PatchChangeRequest ? sender : null
                                )
                        )
                    )
                    .pipe(map(pack()))
                    .subscribe(this.serverHub.output)
            );

            this.subscription.add(
                subscription
            );

            return samplesApp
                .pipe(take(1))
                .pipe(
                    map(
                        value =>
                            new DataChangedEvent(reference, value, "Full", null)
                    )
                )
        }
}