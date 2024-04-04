const basicStoreExample = {
    $type: "OpenSmc.Data.EntityStore",
    reference: {
        $type: "OpenSmc.Layout.LayoutAreaReference",
        area: "Main"
    },
    instances: {
        $areas: {
            Main: {
                $type: "OpenSmc.Layout.Composition.LayoutStackControl",
                dataContext: {
                    $type: "EntityReference",
                    collection: "LineOfBusiness",
                    id: "1"
                },
                areas: [
                    {
                        $type: "EntityReference",
                        collection: "$areas",
                        id: "Main/View1"
                    },
                    {
                        $type: "EntityReference",
                        collection: "$areas",
                        id: "Main/View2"
                    }
                ]
            },
            "Main/View1": {
                $type: "OpenSmc.Layout.Composition.SpinnerControl",
                message: "processing...",
                progress: 0.5
            },
            "Main/View2": {
                $type: "OpenSmc.Layout.Composition.TextBoxControl",
                data: {
                    $type: "Binding",
                    path: "$.DisplayName"
                }
            }
        },
        LineOfBusiness: {
            "1": {
                SystemName: "1",
                DisplayName: "1"
            },
            "2": {
                SystemName: "2",
                DisplayName: "2"
            }
        }
    }
}