import { Binding } from "@open-smc/data/src/contract/Binding";
import { CollectionReference } from "@open-smc/data/src/contract/CollectionReference";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { ClickedEvent } from "@open-smc/layout/src/contract/application.contract";
import { CheckboxControl } from "@open-smc/layout/src/contract/controls/CheckboxControl";
import { HtmlControl } from "@open-smc/layout/src/contract/controls/HtmlControl";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { MenuItemControl } from "@open-smc/layout/src/contract/controls/MenuItemControl";
import { combineLatest, distinctUntilChanged, filter, from, map, of, Subscription, switchMap, tap } from "rxjs";
import { LayoutViews, uiControlType } from "./LayoutViews";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { jsonPatchActionCreator } from "@open-smc/data/src/jsonPatchReducer";
import { DataChangeResponse } from "@open-smc/data/src/contract/DataChangeResponse";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { create, isEqual, keyBy, keys } from "lodash-es";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";
import { handleRequest } from "@open-smc/messaging/src/handleRequest";
import { toJsonPatch } from "@open-smc/data/src/operators/toJsonPatch";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent";
import { pack } from "@open-smc/messaging/src/operators/pack";
import { MessageHub } from "@open-smc/messaging/src/MessageHub";
import { TextBoxControl } from '@open-smc/layout/src/contract/controls/TextBoxControl';
import { v4 } from "uuid";

const smBlue = "#0171ff";

export class SamplesApp {
    store: ReturnType<typeof createTodosStore>;
    subscription = new Subscription();
    private lastMessage: MessageDelivery;

    constructor(serverHub: MessageHub, reference: LayoutAreaReference) {
        this.store = createTodosStore(reference);

        const app = new Workspace({
            page: "home"
        })

        const pages: Record<string, LayoutViews> = {
            home: createHomeLayout(),
            todos: createTodosLayout(this.store),
        };

        const mainLayout = createLayout();

        this.subscription.add(
            mainLayout.subscription
        );

        const pageLayout = app
            .pipe(
                switchMap(
                    state => pages[state.page]
                )
            );

        this.subscription.add(
            combineLatest([mainLayout, pageLayout])
                .pipe(
                    map(([main, page]) => ({ ...main, ...page })),
                    tap(value => console.log(value)),
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
                    const { action, id } = message.payload as { action: 'delete' | 'addTodo' | "nav", id: string };
                    if (action === 'delete') {
                        this.store.update(state => {delete state.collections.todos[id]});
                    }
                    if (action === "addTodo") {
                        const { collections } = this.store.getState();
                        const name = collections.viewBag.newTodo;
                        const id = v4();
                        const newTodo = { id, name, completed: false }
                        this.store.update(state => {state.collections.todos[id] = newTodo});
                    }
                    if (action === "nav") {
                        app.update(state => {state.page = id});
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
            },
        }
    });
}

export type SamplesAppStore = ReturnType<typeof createTodosStore>;

function createLayout() {
    const layoutViews = new LayoutViews();

    layoutViews.addView(
        "/",
        new LayoutStackControl()
            .with({
                skin: "MainWindow",
                areas: [
                    layoutViews.addView(
                        "/Main",
                        new HtmlControl()
                    ),
                    layoutViews.addView(
                        "/Toolbar",
                        new HtmlControl().with({
                            data: "Toolbar"
                        })
                    ),
                    layoutViews.addView(
                        "/SideMenu",
                        new LayoutStackControl().with({
                            skin: "SideMenu",
                            areas: [
                                layoutViews.addView(
                                    null,
                                    new MenuItemControl().with({
                                        title: "Home",
                                        icon: "sm sm-home",
                                        skin: "LargeIcon",
                                        clickMessage: new ClickedEvent({
                                            action: 'nav',
                                            id: "home"
                                        }) as any
                                    })
                                ),
                                layoutViews.addView(
                                    null,
                                    new MenuItemControl().with({
                                        title: "Todos",
                                        skin: "LargeIcon",
                                        icon: "sm sm-check",
                                        clickMessage: new ClickedEvent({
                                            action: 'nav',
                                            id: "todos"
                                        }) as any
                                    })
                                ),
                            ]
                        })
                    )
                ]
            })
    );

    return layoutViews;
}

function createTodosLayout(store: SamplesAppStore) {
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
                            "/todo/name",
                            new HtmlControl()
                                .with({
                                    data: new Binding("$.name")
                                })
                        ),
                        layoutViews.addView(
                            "/todo/completed",
                            new CheckboxControl()
                                .with({
                                    data: new Binding("$.completed")
                                })
                        ),
                        layoutViews.addView(
                            "/todo/deleteButton",
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

    const todosCount = new LayoutStackControl()
        .with({
            skin: "HorizontalPanel",
            areas: [
                layoutViews.addView(undefined, new HtmlControl().with({ data: "Total count:" })),
                layoutViews.addView(
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
        });

    const addTodo = new LayoutStackControl()
        .with({
            skin: "HorizontalPanel",
            areas: [
                layoutViews.addView(
                    null,
                    new TextBoxControl()
                        .with({
                            data: new Binding("$.viewBag.newTodo")
                        })
                ),
                layoutViews.addView(
                    null,
                    new MenuItemControl()
                        .with({
                            title: "Add todo",
                            color: smBlue,
                            clickMessage: new ClickedEvent({
                                action: 'addTodo'
                            }) as any
                        })
                )
            ]
        })

    layoutViews.addView(
        "/Main",
        new LayoutStackControl()
            .with({
                skin: "VerticalPanel",
                areas: [
                    layoutViews.addView(
                        "/addTodo",
                        addTodo
                    ),
                    layoutViews.addView(
                        "/todos",
                        todos
                    ),
                    layoutViews.addView(
                        "/main/todosCount",
                        todosCount
                    )

                ]
            })
    );

    return layoutViews;
}

function createHomeLayout() {
    const layoutViews = new LayoutViews();
    layoutViews.addView(
        "/Main",
        new HtmlControl().with({
            data: "Overview"
        })
    )
    return layoutViews;
}