/*
LayoutAddress(123) - instantiated on UI
RedirectControl(area="", address=new Ifrs17Address())
    => LayoutStack {
            // data host is Ifrs17Address
            views = [
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
                                host: ...
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
                    title: "[0].foo.name"
                }
            ]
        }

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

