import { Binding } from "@open-smc/data/src/contract/Binding";
import { CollectionReference } from "@open-smc/data/src/contract/CollectionReference";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { ClickedEvent } from "@open-smc/layout/src/contract/application.contract";
import { CheckboxControl } from "@open-smc/layout/src/contract/controls/CheckboxControl";
import { HtmlControl } from "@open-smc/layout/src/contract/controls/HtmlControl";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { MenuItemControl } from "@open-smc/layout/src/contract/controls/MenuItemControl";
import { MessageHubBase } from "@open-smc/messaging/src/MessageHubBase";
import { filter, map, of, Subscription } from "rxjs";
import { LayoutViews } from "./LayoutViews";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { jsonPatchActionCreator } from "@open-smc/data/src/jsonPatchReducer";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { isEqual, keyBy, omit } from "lodash-es";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";
import { handleRequest } from "@open-smc/messaging/src/handleRequest";
import { toJsonPatch } from "@open-smc/data/src/operators/toJsonPatch";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent";
import { pack } from "@open-smc/messaging/src/operators/pack";

export const uiControlType = (UiControl as any).$type;

export class Layout {
    store: Workspace<EntityStore>;
    subscription = new Subscription();
    private lastMessage: MessageDelivery;

    constructor(serverHub: MessageHubBase, reference: LayoutAreaReference) {
        const views = this.getLayoutViews();

        this.store = new Workspace({
            reference,
            collections: {
                [uiControlType]: views.toCollection(),
                todos: keyBy(
                    [
                        { id: "1", name: "Task 1", completed: true, },
                        { id: "2", name: "Task 2", completed: false },
                        { id: "3", name: "Task 3", completed: true },
                        { id: "4", name: "Task 4", completed: true },
                    ],
                    'id'
                )
            }
        });

        this.subscription.add(
            serverHub
                .input
                .pipe(
                    filter(messageOfType(PatchChangeRequest)),
                    filter(({ message }) => isEqual(message.reference, reference)),
                    handleRequest(PatchChangeRequest, this.handlePatchChangeRequest())
                )
                .subscribe(serverHub.output)
        );

        this.subscription.add(
            serverHub
                .input
                .pipe(filter(messageOfType(ClickedEvent)))
                .subscribe(({ message }) => {
                    const { action, id } = message.payload as { action: 'delete' | 'edit', id: string };
                    if (action === 'delete') {
                        this.store.next(
                            pathToUpdateAction("/collections/todos")(
                                omit(this.store.getState().collections.todos, id)
                            )
                        );
                    }
                })
        );

        this.subscription.add(
            this.store
                .pipe(toJsonPatch())
                .pipe(
                    map(
                        patch =>
                            new DataChangedEvent(
                                reference,
                                patch,
                                "Patch",
                                this.lastMessage instanceof PatchChangeRequest ? this.lastMessage.sender : null
                            )
                    )
                )
                .pipe(map(pack()))
                .subscribe(serverHub.output)
        );
    }

    private handlePatchChangeRequest =
        () =>
            (delivery: MessageDelivery<PatchChangeRequest>) => {
                this.lastMessage = delivery;
                const { message, sender } = delivery;
                this.store.next(jsonPatchActionCreator(message.change));
                return of(new DataChangeResponse("Committed"));
            }

    private getLayoutViews() {
        const main = new LayoutStackControl()
            .with({
                areas: [
                    new EntityReference(uiControlType, "/main/todos"),
                ]
            });

        const todos = new ItemTemplateControl()
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
            });

        const name = new HtmlControl()
            .with({
                data: new Binding("$.name")
            });

        const completed = new CheckboxControl()
            .with({
                data: new Binding("$.completed")
            });

        const editButton = new MenuItemControl()
            .with({
                icon: "sm sm-edit",
                clickMessage: new ClickedEvent({
                    action: 'edit',
                    id: new Binding("$.id")
                }) as any
            });

        const deleteButton = new MenuItemControl()
            .with({
                icon: "sm sm-close",
                clickMessage: new ClickedEvent({
                    action: 'delete',
                    id: new Binding("$.id")
                }) as any
            });

        const layout = new LayoutViews()
            .addView("/main/todos", todos)
            .addView("/main/todo/name", name)
            .addView("/main/todo/completed", completed)
            .addView("/main/todo/editButton", editButton)
            .addView("/main/todo/deleteButton", deleteButton)
            .addView("/main", main);

        return layout;
    }
}