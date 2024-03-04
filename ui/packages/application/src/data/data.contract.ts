// LayoutAddress(123) - instantiated on UI
// RedirectControl(area="", address=new Ifrs17Address())
//     => LayoutStack
// data host is Ifrs17Address

import { ClickedEvent, RefreshRequest } from "../contract/application.contract";

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

const layoutAddress = new LayoutAddress(123);

post(layoutAddress, new SubscribeDataRequest("OpenSmc.Layout.Area", "$['OpenSmc.Layout.Area']"))
post(layoutAddress, new RefreshRequest("root"));

renderArea("root")

handle(DataChange, () => {
    // patch workspace
    // re-render updated areas tree
    // this dataChange may not contain changes to data collections, then can come later
})

const workspace = {
    "OpenSmc.Layout.Area": [
        {
            id: "root",
            control: {
                $type: "Stack",
                areas: [
                    {
                        $type: "OpenSmc.Data.EntityReference",
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
                        $type: "OpenSmc.Data.EntityReference",
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
                    $type: "OpenSmc.Data.EntityReference",
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


