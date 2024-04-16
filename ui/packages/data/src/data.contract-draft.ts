// LayoutAddress(123) - instantiated on UI
// RedirectControl(area="", address=new Ifrs17Address())
//     => LayoutStack
// data host is Ifrs17Address

import { ClickedEvent, LayoutAddress } from "@open-smc/layout/src/contract/application.contract";
import { SubscribeRequest } from "./contract/SubscribeRequest";
import { DataChangedEvent } from "./contract/DataChangedEvent";
import { PathReferenceBase } from "./contract/PathReferenceBase";
import { EntireWorkspace } from "./contract/EntireWorkspace";
import { LayoutAreaReference } from "./contract/LayoutAreaReference";

const view = {
    title: "Hello",
    color: {
        $type: "Binding",
        path: "myEntity.color"
    }
}


const layoutStackControl = {
    views: [
        {
            $type: "MenuItem",
            address: {
                $type: "UiControlAddress",
                id: "menu",
                host: {
                    $type: "UiControlAddress",
                    id: "stack",
                    host: {
                        $type: "Ifrs17Address",
                        host: {/*...*/}
                    }
                }
            },
            dataContext: [
                {
                    foo: {
                        $id: "foo",
                        $type: "Foo",
                        name: "Bob"
                    }
                }
            ],
            title: "[0].foo.name",
            elements: "data"
        }
    ]
}

/*
control check-in:

1. analyze data context
2. if it contains entity types - start synchronization
3. Detect data-host address from controls address - the first host property not equal to UiControlAddress
4. new SynchronizeEntitiesRequest(dataHostAddress, entities) post to UiAddress
5. maintain my own state by dataHostAddress
6. post the same msg to dataHostAddress
7. wait for response - get most up-to-date instances
 */

const dataContextSample =
    [
        {
            foo: {
                $id: "123",
                $type: "SomeType",
                name: "Alice"
            }
        }
    ];

// -------------- "controls as data" idea

// url: /root

const layoutAddress = new LayoutAddress("123");

// post(layoutAddress, new SubscribeDataRequest())
// post(layoutAddress, new RefreshRequest("root"));

// queryRequest = "root", (/application/dev)

// handle(DataChange, () => {
    // patch workspace
    // re-render updated areas tree
    // this dataChange may not contain changes to data collections, then can come later
// })

const rootControlWorkspaceReference = {
    $type: "OpenSmc.Data.WorkspaceReference",
    address: layoutAddress,
    path: "$['OpenSmc.Layout.LayoutArea'][?@.id=='root']"
}

// root layoutArea pulled from the workspace by queryResult
const layoutArea  = {
    $type: "LayoutArea",
    id: "/path",
    control: {
        $type: "Stack",
        areas: [
            {
                $type: "OpenSmc.Data.WorkspaceReference",
                address: layoutAddress,
                path: "$['OpenSmc.Layout.Area'][?@.id=='root/SideMenu']"
            },
        ]
    }
}

const workspace = {
    "OpenSmc.Layout.Area": [
        {
            id: "root",
            control: {
                $type: "Stack",
                areas: [
                    {
                        $type: "OpenSmc.Data.WorkspaceReference",
                        address: layoutAddress,
                        path: "$['OpenSmc.Layout.Area'][?@.id=='root/SideMenu']"
                    },
                ]
            }
        },
        {
            id: "root/SideMenu",
            control: {
                $type: "Stack",
                dataContext: {
                    foo: "bar"
                },
                areas: [
                    {
                        $type: "OpenSmc.Data.WorkspaceReference",
                        address: layoutAddress,
                        path: "$['OpenSmc.Layout.Area'][?@.id=='root/SideMenu/Button1']"
                    },
                ]
            }
        },
        {
            id: "root/SideMenu/Button1",
            control: {
                $type: "MenuItem",
                dataContext: {
                    $type: "OpenSmc.Data.WorkspaceReference",
                    address: layoutAddress,
                    path: "$['OpenSmc.DataRecords'][?@.systemName=='Hello']"
                },
                title: {
                    $type: "Binding",
                    path: "displayName",
                },
                clickMessage: {
                    $type: "MessageAndAddress",
                    address: layoutAddress,
                    message: new ClickedEvent({
                        $type: "Binding",
                        path: "systemName"
                    })
                }
            }
        }
    ],
    "OpenSmc.DataRecords": [
        {
            id: "1",
            systemName: "Hello",
            displayName: "World"
        }
    ]
}
// main store init
new SubscribeRequest("any string", new EntireWorkspace()); // sent to layout address (to be clarified)


// layout store
// new SubscribeDataRequest("123", new LayoutAreaReference(path_from_url)); // sent to layout address (to be clarified)
// new DataChangedEvent({
//     $type: "LayoutArea", // or "JsonPatch"
//     object: {
//         id: "123",
//         control: {
//             $type: "LayoutStack",
//             areas: [
//                 {
//                     $type: "MenuItemControl",
//                 },
//                 {
//                     $type: "InputBoxControl",
//                     dataContext: {
//                         plainJson: 123,
//                         ref: new WorkspaceReference(), // always reference to the main store
//                     },
//                     value: makeBinding("ref.property") // json path
//                 }
//             ]
//         }
//     } // full thing the first time, json patches afterwards,
// })

// UI creates a store for layout area




const mainStore = {
    entity: {
        property: "123"
    }
}

// a projection from the main store, synchronized via json patches
const layoutStore = {
    $type: "LayoutArea",
    id: "/",
    control: {
        $type: "LayoutStack",
        skin: "MainWindow",
        areas: [
            {
                $type: "LayoutArea",
                id: "/Main",
                control: {
                    $type: "MenuItemControl",
                    id: "123",
                    // to be clarified where to send clicks
                }
            },
            {
                $type: "LayoutArea",
                id: "/Toolbar",
                control: {
                    $type: "InputBoxControl",
                    dataContext: {
                        plainJson: 123,
                        // data: new WorkspaceReference("entity"), // reference to the main store
                    },
                    // value: makeBinding("data.property") // JsonPath
                }
            }
        ]
    }
}

// map serialization

const myMap = new Map();

const objectKey = {name: "foo"};

myMap.set(objectKey, "1");
myMap.set("bar", "2");

const path = [objectKey];

const path2 = "myType/hello"; // ["myType", "hello"]

"myType/hello" => ["myType", "hello"]

const myMapSerialized = {
    $type: "Map",
    entries: [
        [{name: "foo"}, "1"],
        ["bar", "2"]
    ]
}



const state = {
    child1: {
        foo: "2",
    },
    child2: {
        foo: "2"
    }
}