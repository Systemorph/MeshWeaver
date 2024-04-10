export const basicStoreExample = {
    $type: "OpenSmc.Data.EntityStore",
    reference: {
        $type: "OpenSmc.Data.LayoutAreaReference",
        area: "MainWindow"
    },
    instances: {
        "OpenSmc.Layout.UiControl": {
            MainWindow: {
                $type: "OpenSmc.Layout.Composition.LayoutStackControl",
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
            "Main": {
                $type: "OpenSmc.Layout.Composition.SpinnerControl",
                message: "processing...",
                progress: 0.5
            },
            "Toolbar": {
                $type: "OpenSmc.Layout.TextBoxControl",
                dataContext: {
                    $type: "OpenSmc.Data.EntityReference",
                    collection: "LineOfBusiness",
                    id: "1"
                },
                data: {
                    $type: "OpenSmc.Layout.DataBinding.Binding",
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