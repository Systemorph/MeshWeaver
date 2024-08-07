export const basicStoreExample = {
    $type: "MeshWeaver.Data.EntityStore",
    reference: {
        $type: "MeshWeaver.Data.LayoutAreaReference",
        area: "MainWindow"
    },
    collections: {
        "MeshWeaver.Layout.UiControl": {
            MainWindow: {
                $type: "MeshWeaver.Layout.Composition.LayoutStackControl",
                skin: "MainWindow",
                areas: [
                    {
                        $type: "MeshWeaver.Data.EntityReference",
                        collection: "MeshWeaver.Layout.UiControl",
                        id: "Main"
                    },
                    {
                        $type: "MeshWeaver.Data.EntityReference",
                        collection: "MeshWeaver.Layout.UiControl",
                        id: "Toolbar"
                    },
                    {
                        $type: "MeshWeaver.Data.EntityReference",
                        collection: "MeshWeaver.Layout.UiControl",
                        id: "ContextMenu"
                    }
                ]
            },
            "Main": {
                $type: "MeshWeaver.Layout.Composition.SpinnerControl",
                message: "processing...",
                progress: 0.5
            },
            "Toolbar": {
                $type: "MeshWeaver.Layout.TextBoxControl",
                dataContext: {
                    $type: "MeshWeaver.Data.EntityReference",
                    collection: "LineOfBusiness",
                    id: "1"
                },
                data: {
                    $type: "MeshWeaver.Layout.DataBinding.Binding",
                    path: "$.DisplayName"
                }
            },
            "ContextMenu": {
                $type: "MeshWeaver.Layout.Views.MenuItemControl",
                dataContext: {
                    $type: "MeshWeaver.Data.JsonPathReference",
                    path: "$.LineOfBusiness.1"
                },
                title: {
                    $type: "MeshWeaver.Layout.DataBinding.Binding",
                    path: "$.DisplayName"
                },
                icon: "systemorph-fill"
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