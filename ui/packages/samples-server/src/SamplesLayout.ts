import { Binding } from "@open-smc/data/src/contract/Binding";
import { CollectionReference } from "@open-smc/data/src/contract/CollectionReference";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { ClickedEvent } from "@open-smc/layout/src/contract/application.contract";
import { CheckboxControl } from "@open-smc/layout/src/contract/controls/CheckboxControl";
import { HtmlControl } from "@open-smc/layout/src/contract/controls/HtmlControl";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { MenuItemControl } from "@open-smc/layout/src/contract/controls/MenuItemControl";
import { distinctUntilChanged, filter, from, map, of, Subscription } from "rxjs";
import { LayoutViews, uiControlType } from "./LayoutViews";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { jsonPatchActionCreator } from "@open-smc/data/src/jsonPatchReducer";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { create, isEqual, keyBy, keys, omit, tap } from "lodash-es";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";
import { handleRequest } from "@open-smc/messaging/src/handleRequest";
import { toJsonPatch } from "@open-smc/data/src/operators/toJsonPatch";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent";
import { pack } from "@open-smc/messaging/src/operators/pack";
import { MessageHub } from "@open-smc/messaging/src/MessageHub";
import { TextBoxControl } from '@open-smc/layout/src/contract/controls/TextBoxControl';
import { v4 } from "uuid";

export class SamplesLayout {
    store: ReturnType<typeof createTodosStore>;
    subscription = new Subscription();
    private lastMessage: MessageDelivery;

    constructor(serverHub: MessageHub, reference: LayoutAreaReference) {
        this.store = createTodosStore(reference);

        const views = createLayout(this.store);

        this.subscription.add(
            views.subscription
        );

        this.subscription.add(
            views
                .pipe(
                    map(pathToUpdateAction(`/collections/${uiControlType}`))
                )
                .subscribe(this.store)
        );

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
                    const { action, id } = message.payload as { action: 'delete' | 'addTodo', id: string };
                    if (action === 'delete') {
                        this.store.next(
                            pathToUpdateAction("/collections/todos")(
                                omit(this.store.getState().collections.todos, id)
                            )
                        );
                    }
                    if (action === "addTodo") {
                        const { collections } = this.store.getState();
                        const name = collections.viewBag.newTodo;
                        const id = v4();
                        const newTodo = { id, name, completed: false }
                        const { todos } = collections;

                        this.store.next(
                            pathToUpdateAction("/collections/todos")(
                                {
                                    ...todos,
                                    [id]: newTodo
                                }
                            )
                        )
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
}

function createTodosStore(reference: LayoutAreaReference) {
    return new Workspace({
        reference,
        collections: {
            todos: keyBy(
                [
                    { id: "1", name: "Task 1", completed: true, },
                    { id: "2", name: "Task 2", completed: false },
                    { id: "3", name: "Task 3", completed: true },
                    { id: "4", name: "Task 4", completed: true },
                ],
                'id'
            ),
            viewBag: {
                newTodo: "New task"
            }
        }
    });
}

export type TodosStore = ReturnType<typeof createTodosStore>;

function createLayout(store: TodosStore) {
    const layoutViews = new LayoutViews();

    const todos = new ItemTemplateControl()
        .with({
            dataContext: new CollectionReference("todos"),
            data: new Binding("$"),
            view: new LayoutStackControl()
                .with({
                    skin: "HorizontalPanel",
                    areas: [
                        layoutViews.addView(
                            "/main/todo/name",
                            new HtmlControl()
                                .with({
                                    data: new Binding("$.name")
                                })
                        ),
                        layoutViews.addView(
                            "/main/todo/completed",
                            new CheckboxControl()
                                .with({
                                    data: new Binding("$.completed")
                                })
                        ),
                        layoutViews.addView(
                            "/main/todo/deleteButton",
                            new MenuItemControl()
                                .with({
                                    icon: "sm sm-close",
                                    clickMessage: new ClickedEvent({
                                        action: 'delete',
                                        id: new Binding("$.id")
                                    }) as any
                                })
                        ),
                    ]
                })
        });

    const addTodo = new LayoutStackControl()
        .with({
            skin: "HorizontalPanel",
            areas: [
                layoutViews.addView(
                    "/addTodo/textbox",
                    new TextBoxControl()
                        .with({
                            data: new Binding("$.viewBag.newTodo")
                        })
                ),
                layoutViews.addView(
                    "/addTodo/addButton",
                    new MenuItemControl()
                        .with({
                            title: "Add todo",
                            color: "#0171ff",
                            clickMessage: new ClickedEvent({
                                action: 'addTodo'
                            }) as any
                        })
                )
            ]
        })

    layoutViews.addView(
        "/",
        new LayoutStackControl()
            .with({
                skin: "MainWindow",
                areas: [
                    layoutViews.addView(
                        "/Main",
                        new LayoutStackControl()
                            .with({
                                areas: [
                                    layoutViews.addView(
                                        "/main/todos",
                                        todos
                                    ),
                                    layoutViews.addView(
                                        "/main/todosCount",
                                        new LayoutStackControl()
                                            .with({
                                                skin: "HorizontalPanel",
                                                areas: [
                                                    layoutViews.addView(undefined, new HtmlControl().with({ data: "Total count:" })),
                                                    layoutViews.addViewStream(
                                                        undefined,
                                                        store.pipe(
                                                            map(
                                                                state =>
                                                                    new HtmlControl()
                                                                        .with({ data: keys(state.collections.todos)?.length })
                                                            )
                                                        )
                                                    )
                                                ]
                                            })
                                    )
                                ]
                            })
                    ),
                    layoutViews.addView(
                        "/Toolbar",
                        new LayoutStackControl()
                            .with({
                                skin: "HorizontalPanel",
                                areas: [
                                    layoutViews.addView(
                                        "/toolbar/",
                                        new LayoutStackControl()
                                            .with({
                                                skin: "HorizontalPanel",
                                                areas: [
                                                    layoutViews.addView(
                                                        "/toolbar/addTodo",
                                                        addTodo
                                                    )
                                                ]
                                            })
                                    )
                                ]
                            })
                    )
                ]
            })
    );

    return layoutViews;
}