import { filter, map, Observable, Observer, of, skip, Subject, Subscription, take } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest.ts";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference.ts";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest.ts";
import { isEqual, keyBy } from "lodash-es";
import { handleRequest } from "@open-smc/messaging/src/handleRequest.ts";
import { Workspace } from "@open-smc/data/src/Workspace.ts";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore.ts";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse.ts";
import { UnsubscribeDataRequest } from "@open-smc/data/src/contract/UnsubscribeDataRequest.ts";
import { HtmlControl } from "@open-smc/layout/src/contract/controls/HtmlControl.ts";
import { Layout, uiControlType } from "./Layout.ts";
import { jsonPatchActionCreator } from "@open-smc/data/src/jsonPatchReducer.ts";
import { toJsonPatch } from "@open-smc/data/src/operators/toJsonPatch.ts";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent.ts";
import { pack } from "@open-smc/messaging/src/operators/pack.ts";
import { TextBoxControl } from "@open-smc/layout/src/contract/controls/TextBoxControl.ts";
import { Binding } from "@open-smc/data/src/contract/Binding.ts";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference.ts";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl.ts";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl.ts";
import { CollectionReference } from "@open-smc/data/src/contract/CollectionReference.ts";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType.ts";
import { CheckboxControl } from "@open-smc/layout/src/contract/controls/CheckboxControl.ts";

const todos = [
    {id: "1", name: "Task 1", completed: true, },
    {id: "2", name: "Task 2", completed: false },
    {id: "3", name: "Task 3", completed: true},
    {id: "4", name: "Task 4", completed: true},
]

export class SamplesServer extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    subscription = new Subscription();
    protected input = new Subject<MessageDelivery>();
    protected output = new Subject<MessageDelivery>();

    private data = new Workspace({
        todos: keyBy(todos, 'id')
    });

    constructor() {
        super(subscriber => this.output.subscribe(subscriber));

        this.subscription.add(
            this.input
                .pipe(handleRequest(SubscribeRequest, this.subscribeRequestHandler()))
                .subscribe(this.output)
        );
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: MessageDelivery) {
        this.input.next(value);
    }

    subscribeRequestHandler = () =>
        ({message, sender}: MessageDelivery<SubscribeRequest>) => {
            const {reference} = message;

            const subscription = new Subscription();

            const entityStore =
                this.render(reference as LayoutAreaReference, subscription);

            subscription.add(
                this.input
                    .pipe(
                        filter(messageOfType(PatchChangeRequest)),
                        filter(({message}) => isEqual(message.reference, reference)),
                        handleRequest(PatchChangeRequest, this.handlePatchChangeRequest(entityStore))
                    )
                    .subscribe(this.output)
            );

            subscription.add(
                this.input
                    .pipe(
                        filter(messageOfType(UnsubscribeDataRequest)),
                        filter(({message}) => isEqual(message.reference, reference))
                    )
                    .subscribe(request => {
                        subscription.unsubscribe();
                    })
            );

            const change$ =
                entityStore
                    .pipe(toJsonPatch())
                    .pipe(
                        map(
                            patch =>
                                new DataChangedEvent(reference, patch, "Patch", sender)
                        )
                    );

            subscription.add(
                change$
                    .pipe(map(pack()))
                    .subscribe(this.output)
            );

            this.subscription.add(subscription);

            return entityStore
                .pipe(take(1))
                .pipe(
                    map(
                        value =>
                            new DataChangedEvent(reference, value, "Full", null)
                    )
                )
        }

    private handlePatchChangeRequest =
        (workspace: Workspace<EntityStore>) =>
            ({message, sender}: MessageDelivery<PatchChangeRequest>) => {
                workspace.next(jsonPatchActionCreator(message.change));
                return of(new DataChangeResponse("Committed"));
            }


    private render(reference: LayoutAreaReference, subscription: Subscription) {
        const layout = this.getLayout();

        const entityStore =
            new Workspace<EntityStore>(
                {
                    reference,
                    collections: {
                        [uiControlType]: layout.views,
                        todos: this.data.getState().todos
                    }
                }
            );

        return entityStore;
    }

    private getLayout() {
        const textBox = new TextBoxControl(new Binding("$.name"));
        textBox.dataContext = new EntityReference("todos", "1");

        const html = new HtmlControl(new Binding("$.name"));
        html.dataContext = new EntityReference("todos", "1");

        const stack = new LayoutStackControl();

        stack.areas = [
            new EntityReference(uiControlType, "/main/todos"),
            new EntityReference(uiControlType, "/main/textBox"),
        ];

        const todos = new ItemTemplateControl();

        const todoItem = new LayoutStackControl();

        todoItem.areas = [
            new EntityReference(uiControlType, "/main/todo/name"),
            new EntityReference(uiControlType, "/main/todo/completed"),
        ];

        todoItem.skin = "HorizontalPanel";

        todos.data = new Binding("$");
        todos.dataContext = new CollectionReference("todos");
        todos.view = todoItem;

        const name = new HtmlControl(new Binding("$.name"))
        const completed = new CheckboxControl(new Binding("$.completed"))

        const layout = new Layout()
            .addView("/main/textBox", textBox)
            .addView("/main/todos", todos)
            .addView("/main/todo", todoItem)
            .addView("/main/todo/name", name)
            .addView("/main/todo/completed", completed)
            .addView("/main", stack);

        return layout;
    }
}