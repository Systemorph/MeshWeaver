import { distinctUntilChanged, filter, map, Observable, Observer, of, skip, Subject, Subscription, take } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest.ts";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference.ts";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest.ts";
import { isEqual, keyBy, omit } from "lodash-es";
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
import { MenuItemControl } from "@open-smc/layout/src/contract/controls/MenuItemControl.ts";
import { ClickedEvent } from "@open-smc/layout/src/contract/application.contract.ts";
import { updateByReferenceActionCreator } from "@open-smc/data/src/workspaceReducer.ts";
import { PathReference } from "@open-smc/data/src/contract/PathReference.ts";
import { log } from "@open-smc/utils/src/operators/log.ts";
import { sliceByReference } from "@open-smc/data/src/sliceByReference.ts";
import { selectByPath } from "@open-smc/data/src/operators/selectByPath.ts";
import { updateByPath } from "@open-smc/data/src/operators/updateByPath.ts";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction.ts";

const todos = [
    {id: "1", name: "Task 1", completed: true,},
    {id: "2", name: "Task 2", completed: false},
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

            subscription.add(
                this.input
                    .pipe(filter(messageOfType(ClickedEvent)))
                    .subscribe(({message}) => {
                        const {action, id} = message.payload as { action: 'delete' | 'edit', id: string };
                        if (action === 'delete') {
                            this.data.next(
                                pathToUpdateAction("/todos")(omit(this.data.getState().todos, id))
                            );
                        }
                    })
            )

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
                    .pipe(log('change'))
                    .pipe(
                        map(
                            patch =>
                                new DataChangedEvent(
                                    reference,
                                    patch,
                                    "Patch",
                                     jsonPatchActionCreator.match(entityStore.lastAction) ? sender : null
                                )
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
                    }
                }
            );

        subscription.add(
            this.data
                .pipe(
                    map(selectByPath("/todos")),
                    distinctUntilChanged(),
                    map(pathToUpdateAction("/collections/todos"))
                )
                .subscribe(entityStore)
        );

        return entityStore;
    }

    private getLayout() {
        const main =
            new LayoutStackControl()
                .with({
                    areas: [
                        new EntityReference(uiControlType, "/main/todos"),
                    ]
                });

        const todos =
            new ItemTemplateControl()
                .with({
                    dataContext: new CollectionReference("todos"),
                    data: new Binding("$"),
                    view: new LayoutStackControl()
                        .with({
                            skin: "HorizontalPanel",
                            areas: [
                                new EntityReference(uiControlType, "/main/todo/name"),
                                new EntityReference(uiControlType, "/main/todo/completed"),
                                new EntityReference(uiControlType, "/main/todo/editButton"),
                                new EntityReference(uiControlType, "/main/todo/deleteButton"),
                            ]
                        })
                })

        const name =
            new HtmlControl()
                .with({
                    data: new Binding("$.name")
                })

        const completed =
            new CheckboxControl()
                .with({
                    data: new Binding("$.completed")
                })

        const editButton =
            new MenuItemControl()
                .with({
                    icon: "sm sm-edit",
                    clickMessage:
                        new ClickedEvent({
                            action: 'edit',
                            id: new Binding("$.id")
                        }) as any
                });

        const deleteButton =
            new MenuItemControl()
                .with({
                    icon: "sm sm-close",
                    clickMessage: new ClickedEvent({
                        action: 'delete',
                        id: new Binding("$.id")
                    }) as any
                })

        const layout = new Layout()
            .addView("/main/todos", todos)
            .addView("/main/todo/name", name)
            .addView("/main/todo/completed", completed)
            .addView("/main/todo/editButton", editButton)
            .addView("/main/todo/deleteButton", deleteButton)
            .addView("/main", main);

        return layout;
    }
}