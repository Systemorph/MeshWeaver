import { ClickedEvent, LayoutArea } from "@open-smc/application/src/contract/application.contract";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { RootState } from "../../../application/src/app/store";
import { WorkspaceReference } from "@open-smc/data/src/data.contract";

const layoutAddress = "layoutAddress";

export const rootReference
    = new WorkspaceReference(layoutAddress, "$['OpenSmc.Layout.LayoutArea'][?@.id=='root']");

const sideMenuReference =
    new WorkspaceReference(layoutAddress, "$['OpenSmc.Layout.LayoutArea'][?@.id=='root/SideMenu']");

const inputBoxReference =
    new WorkspaceReference(layoutAddress, "$['OpenSmc.Layout.LayoutArea'][?@.id=='root/SideMenu/InputBox']");

/*
 actions:
 - applyClientPatch(areaRef
 */

const state = {
    rootKey,
    areas: {
        [rootKey]: {
            id: "root",
            control: {
                $type: "LayoutStackControl",
                areas: [
                    {
                        $type: "OpenSmc.Layout.LayoutArea",
                        id: "plainArea",
                        control: {
                            // ...
                        }
                    },
                    sideMenuReference
                ]
            }
        },
        [inputBoxReference]: {
            id: "root/SideMenu/InputBox",
            control: {
                $type: "InputBoxControl",
                value: "123"
            }
        }
    }
}

export const initialState: RootState = {
    data: {
        workspace: {
            "OpenSmc.Layout.LayoutArea": [
                {
                    id: "root",
                    control: {
                        $type: "LayoutStackControl",
                        areas: [
                            {
                                $type: "OpenSmc.Layout.LayoutArea",
                                id: "plainArea",
                                dataContext: someRef,
                                control: {
                                    // ...
                                }
                            },
                            sideMenuReference
                        ]
                    }
                },
                {
                    id: "root/sideMenu",
                    control: {
                        $type: "LayoutStackControl",
                        areas: [
                            new WorkspaceReference(layoutAddress, "$['OpenSmc.Layout.LayoutArea'][?@.id=='root/SideMenu/Button1']")
                        ]
                    }
                },
                {
                    id: "root/SideMenu/Button1",
                    control: {
                        $type: "MenuItemControl",
                        dataContext: new WorkspaceReference(layoutAddress, "$['OpenSmc.DataRecords'][?@.systemName=='hello']"),
                        title: makeBinding("displayName"),
                        clickMessage: {
                            $type: "MessageAndAddress",
                            address: layoutAddress,
                            message: new ClickedEvent(makeBinding("systemName"))
                        }
                    },
                    renderedControl: {
                        // ...
                    }
                },
                {
                    id: "root/SideMenu/InputBox",
                    control: {
                        $type: "InputBox",
                        dataContext: new WorkspaceReference(layoutAddress, "$['OpenSmc.DataRecords'][?@.systemName=='hello']"),
                        value: makeBinding("child.displayName") // set("value", "new value")
                    }
                },
                {
                    id: "root/SideMenu/NotebookEditor",
                    control: {
                        $type: "NotebookEditor",
                        dataContext: {
                            plainJson: {
                                name: "Foo"
                            },
                            child: new WorkspaceReference(layoutAddress, "$['OpenSmc.DataRecords'][?@.systemName=='notebook-editor']")
                        },
                        elements: makeBinding("child.elements") // set("elements", [...])
                    }
                }
            ],
            "OpenSmc.DataRecords": [
                {
                    id: "1",
                    systemName: "hello",
                    displayName: "Hello world"
                },
                {
                    id: "2",
                    systemName: "notebook-editor",
                    elements: [
                        // ...
                    ]
                }
            ]
        }
    }
}

// view => value
// dataContext => child.displayName
//

const parentContext = {
    categories: new WorkspaceReference(layoutAddress, "$['OpenSmc.DataRecords']"),
    users: [
        {
            name: "Alice"
        }
    ]
}

// subscribe to workspace by path $['OpenSmc.DataRecords'] => categories
// subscribe to parentContext by path $['categories'] =>

const dataContext = {
    item: {
        category: makeBinding("categories[0]")
    },
    __prototype: parentContext
}

resolve(dataContext, "item.category.name"); // "Foo"
resolve(dataContext, "users[0].name"); // "Alice"

const dataContext = {
    plainJson: {
        name: "Foo"
    },
    workspaceRef: new WorkspaceReference(layoutAddress, "$['OpenSmc.DataRecords'][?@.systemName=='notebook-editor']")
}

