import { filter, map, Observable, Observer, of, Subject, Subscription } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest.ts";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference.ts";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest.ts";
import { isEqual, keyBy } from "lodash-es";
import { handleRequest } from "@open-smc/messaging/src/handleRequest.ts";
import { Workspace } from "@open-smc/data/src/Workspace.ts";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore.ts";
import { Collection } from "@open-smc/data/src/contract/Collection.ts";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction.ts";
import { toChangeStream } from "@open-smc/data/src/operators/toChangeStream.ts";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse.ts";
import { UnsubscribeDataRequest } from "@open-smc/data/src/contract/UnsubscribeDataRequest.ts";
import { HtmlControl } from "@open-smc/layout/src/contract/controls/HtmlControl.ts";
import { Layout } from "./Layout.ts";
import { jsonPatchActionCreator } from "@open-smc/data/src/jsonPatchReducer.ts";

const todos = [
    {name: "Go shopping", completed: false},
    {name: "Cleanup the desk", completed: false},
]

export class SamplesServer extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    subscription = new Subscription();
    protected input = new Subject<MessageDelivery>();
    protected output = new Subject<MessageDelivery>();

    private data = new Workspace({
        todos: keyBy(todos, 'name')
    });

    private layout: Layout;

    constructor() {
        super(subscriber => this.output.subscribe(subscriber));

        this.subscription.add(
            this.input
                .pipe(handleRequest(SubscribeRequest, this.subscribeRequestHandler()))
                .subscribe(this.output)
        );

        this.layout =
            new Layout()
                .addView("Main", new HtmlControl("Hello world"));
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
                const entityStoreWorkspace = new Workspace<EntityStore>();

                const subscription = new Subscription();

                subscription.add(
                    this.layout.render(this.data, reference as LayoutAreaReference)
                        .pipe(map(pathToUpdateAction("")))
                        .subscribe(entityStoreWorkspace)
                );

                subscription.add(
                    this.input
                        .pipe(filter(({message}) =>
                            message instanceof PatchChangeRequest && isEqual(message.reference, reference)))
                        .pipe(handleRequest(PatchChangeRequest, this.handlePatchChangeRequest(entityStoreWorkspace)))
                        .subscribe(this.output)
                );

                subscription.add(
                    this.input
                        .pipe(filter(({message}) =>
                            message instanceof UnsubscribeDataRequest && isEqual(message.reference, reference)))
                        .subscribe(request => {
                            subscription.unsubscribe();
                        })
                );

                this.subscription.add(subscription);

                return entityStoreWorkspace.pipe(toChangeStream(reference));
            }
        }

    private handlePatchChangeRequest = (workspace: Workspace<EntityStore>) =>
        (message: PatchChangeRequest) => {
            workspace.next(jsonPatchActionCreator(message.change));
            return of(new DataChangeResponse("Committed"));
        }
}