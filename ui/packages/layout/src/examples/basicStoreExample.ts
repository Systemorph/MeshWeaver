export const basicStoreExample = {
    $type: "OpenSmc.Data.EntityStore",
    reference: {
        $type: "OpenSmc.Layout.LayoutAreaReference",
        area: "MainWindow"
    },
    collections: {
        "OpenSmc.Layout.UiControl": [
            {
                $type: "OpenSmc.Layout.Composition.LayoutStackControl",
                $id: "MainWindow",
                skin: "MainWindow",
                areas: [
                    {
                        $type: "OpenSmc.Data.EntityReference",
                        collection: "OpenSmc.Layout.UiControl",
                        id: "Main"
                    },
                    {
                        $type: "OpenSmc.Data.EntityReference",
                        collection: "OpenSmc.Layout.UiControl",
                        id: "Toolbar"
                    }
                ]
            },
            {
                $type: "OpenSmc.Layout.Composition.SpinnerControl",
                $id: "Main",
                message: "processing...",
                progress: 0.5
            },
            {
                $type: "OpenSmc.Layout.TextBoxControl",
                $id: "Toolbar",
                dataContext: {
                    $type: "OpenSmc.Data.EntityReference",
                    collection: "DataCube",
                    id: {
                        lineOfBusiness: "1",
                        currency: "CHF"
                    }
                },
                data: {
                    $type: "OpenSmc.Layout.DataBinding.Binding",
                    path: "$.DisplayName"
                }
            }
        ],
        LineOfBusiness: [
            {
                SystemName: "1",
                DisplayName: "1"
            },
            {
                SystemName: "2",
                DisplayName: "2"
            }
        ],
        DataCube: [
            {
                $type: "DataCube",
                $id: {
                    lineOfBusiness: "1",
                    currency: "CHF"
                },
                DisplayName: "1",
                Value: 42,
            }
        ]
    }
}