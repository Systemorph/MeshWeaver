import {
    filter,
    map,
    Subscription,
    take} from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest.ts";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference.ts";
import { isEqual } from "lodash-es";
import { handleRequest } from "@open-smc/messaging/src/handleRequest.ts";
import { UnsubscribeDataRequest } from "@open-smc/data/src/contract/UnsubscribeDataRequest.ts";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent.ts";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType.ts";
import { MessageHubBase } from "@open-smc/messaging/src/MessageHubBase.ts";
import { Layout } from "./Layout";

export class SamplesApp {
    subscription = new Subscription();

    constructor(private serverHub: MessageHubBase) {
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

            const layout = new Layout(this.serverHub, reference as LayoutAreaReference);

            subscription.add(
                layout.subscription
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

            this.subscription.add(
                subscription
            );

            return layout
                .store
                .pipe(take(1))
                .pipe(
                    map(
                        value =>
                            new DataChangedEvent(reference, value, "Full", null)
                    )
                )
        }
}