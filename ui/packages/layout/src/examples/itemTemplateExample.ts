export const itemTemplateExample = {
    $type: "OpenSmc.Data.EntityStore",
    reference: {
        $type: "OpenSmc.Data.LayoutAreaReference",
        area: "Main"
    },
    collections: {
        "OpenSmc.Layout.UiControl": {
            "Main": {
                $type: "OpenSmc.Layout.ItemTemplateControl",
                dataContext: {
                    $type: "OpenSmc.Data.CollectionReference",
                    collection: "LineOfBusiness"
                },
                data: {
                    $type: "OpenSmc.Layout.DataBinding.Binding",
                    path: "$"
                },
                view: {
                    $type: "OpenSmc.Layout.Composition.LayoutStackControl",
                    areas: [
                        {
                            $type: "OpenSmc.Data.EntityReference",
                            collection: "OpenSmc.Layout.UiControl",
                            id: "Main/DisplayName"
                        }
                    ]
                }
            },
            "Main/DisplayName": {
                $type: "OpenSmc.Layout.TextBoxControl",
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