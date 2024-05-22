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
import { combineLatest, distinctUntilChanged, filter, map, Subscription, switchMap, tap } from "rxjs";
import { LayoutViews, uiControlType } from "./LayoutViews";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { keyBy, keys, values } from "lodash-es";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";
import { MessageHub } from "@open-smc/messaging/src/MessageHub";
import { TextBoxControl } from '@open-smc/layout/src/contract/controls/TextBoxControl';
import { v4 } from "uuid";
import { log } from "@open-smc/utils/src/operators/log";

const smBlue = "#0171ff";

function getState(reference: LayoutAreaReference) {
    return {
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
                newTodo: ""
            },
        }
    }
}

export type SamplesStore = ReturnType<typeof getState>;

export class SamplesApp extends Workspace<SamplesStore> {
    subscription = new Subscription();
    layouts: Record<string, LayoutViews>;

    appState = new Workspace({
        pages: {
            home: {
                icon: "sm sm-home",
                title: "home",
            },
            todos: {
                icon: "sm sm-check",
                title: "Todos",
            },
            multiselect: {
                icon: "sm sm-slice",
                title: "Multiselect",
            },
        },
        page: "home"
    })

    constructor(serverHub: MessageHub, reference: LayoutAreaReference) {
        super(null);

        this.update(() => getState(reference));

        this.layouts = {
            home: this.createHomeLayout(),
            todos: this.createTodosLayout(),
            multiselect: this.createMultiselectLayout()
        }

        const mainLayout = this.createLayout();

        this.subscription.add(
            mainLayout.subscription
        );

        const pageLayout = this.appState
            .pipe(
                switchMap(
                    state => this.layouts[state.page]
                )
            );

        this.subscription.add(
            combineLatest([mainLayout, pageLayout])
                .pipe(
                    map(([main, page]) => ({ ...main, ...page })),
                    map(pathToUpdateAction(`/collections/${uiControlType}`))
                )
                .subscribe(this)
        );

        this.subscription.add(
            serverHub
                .input
                .pipe(filter(messageOfType(ClickedEvent)))
                .subscribe(({ message }) => {
                    const { action, id } = message.payload as { action: 'delete' | 'addTodo' | "nav", id: string };
                    if (action === 'delete') {
                        this.update(state => { delete state.collections.todos[id] });
                    }
                    if (action === "addTodo") {
                        const { collections } = this.getState();
                        const name = collections.viewBag.newTodo;
                        if (name) {
                            const id = v4().substring(0, 2);
                            const newTodo = { id, name, completed: false }
                            this.update(state => {
                                const newTodos = values(state.collections.todos).concat(newTodo);
                                state.collections.todos = keyBy(newTodos, 'id');
                                state.collections.viewBag.newTodo = "";
                            });
                        }
                    }
                    if (action === "nav") {
                        this.appState.update(state => { state.page = id });
                    }
                })
        );
    }

    createLayout() {
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
                            this.appState.pipe(
                                map(state => state.pages),
                                distinctUntilChanged(),
                                map(
                                    pages =>
                                        new LayoutStackControl().with({
                                            skin: "SideMenu",
                                            areas:
                                                keys(pages).map(page => {
                                                    const { icon, title } = pages[page];

                                                    return layoutViews.addView(
                                                        `/SideMenu/${page}`,
                                                        new MenuItemControl().with({
                                                            title,
                                                            icon,
                                                            skin: "LargeIcon",
                                                            clickMessage: new ClickedEvent({
                                                                action: 'nav',
                                                                id: page
                                                            }) as any
                                                        })
                                                    )
                                                })
                                        })
                                )
                            )
                        )
                    ]
                })
        );

        return layoutViews;
    }

    createTodosLayout() {
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

        layoutViews.addView(
            "/Main",
            new LayoutStackControl()
                .with({
                    skin: "VerticalPanel",
                    areas: [
                        layoutViews.addView(
                            "/todos",
                            todos
                        )
                    ]
                }),
        );

        const todosCount = new LayoutStackControl()
            .with({
                skin: "HorizontalPanel",
                areas: [
                    layoutViews.addView(
                        "/totalLabel",
                        new HtmlControl().with({ data: "Total:" })
                    ),
                    layoutViews.addView(
                        "/totalCount",
                        this.pipe(
                            map(
                                state =>
                                    new HtmlControl()
                                        .with({ 
                                            data: keys(state.collections.todos)?.length 
                                        })
                            )
                        )
                    )
                ]
            });

        const toolbar = new LayoutStackControl()
            .with({
                skin: "HorizontalPanel",
                areas: [
                    layoutViews.addView(
                        "/newTodoName",
                        new TextBoxControl()
                            .with({
                                placeholder: "New todo",
                                data: new Binding("$.viewBag.newTodo")
                            })
                    ),
                    layoutViews.addView(
                        "/AddButton",
                        new MenuItemControl()
                            .with({
                                title: "Add todo",
                                color: smBlue,
                                clickMessage: new ClickedEvent({
                                    action: 'addTodo'
                                }) as any
                            })
                    ),
                    layoutViews.addView(
                        "/todosTotal",
                        todosCount
                    )
                ]
            })

        layoutViews.addView(
            "/Toolbar",
            toolbar
        )

        return layoutViews;
    }

    createHomeLayout() {
        const layoutViews = new LayoutViews();
        layoutViews.addView(
            "/Main",
            new HtmlControl().with({
                data: "Overview"
            })
        )
        return layoutViews;
    }

    createMultiselectLayout() {
        const layoutViews = new LayoutViews();

        layoutViews.addView(
            "/Main",
            new LayoutStackControl().with({
                areas: [
                    layoutViews.addView(
                        null,
                        new TextBoxControl().with({
                            data: "Multiselect"
                        })
                    )
                ]
            })
        )

        return layoutViews;
    }
}